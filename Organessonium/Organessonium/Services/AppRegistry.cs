using System.Collections.Concurrent;
using System.IO;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

public sealed record LibraryStatistics(long FileCount, long FolderCount, long TotalBytes);

public sealed partial class AppRegistry
{
    public const string RootFolderId = "root";
    public const string SynchronizedRootFolderId = "synchronized";
    public const string SynchronizedRootPath = "/@synchronized";
    public const string TrashFolderId = "trash";
    public const string TrashPath = "/@trash";

    private readonly DiscordApiClient _discord;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly MetadataService _metadata;
    private readonly SerialIndexService _serials;
    private readonly ConcurrentDictionary<string, FolderIndexMapping> _mappings;
    private readonly ConcurrentDictionary<string, FolderDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<List<AppEntry>>> _folderRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _folderFileCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LibraryStatistics> _libraryStatistics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _deferredFolderSerials = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _duplicateFolderRepairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _mappingRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _rootCreationGate = new(1, 1);
    private readonly SemaphoreSlim _entryMutationGate = new(1, 1);
    private readonly SemaphoreSlim _serialSynchronizationGate = new(1, 1);
    private readonly SemaphoreSlim _serverIndexRefreshGate = new(1, 1);
    private int _remoteSerialCleanupStarted;
    internal SharedStorageManager SharedStorage { get; }

    public AppRegistry(DiscordApiClient discord, SettingsStore store, AppSettings settings)
    {
        _discord = discord;
        _store = store;
        _settings = settings;
        _metadata = new MetadataService(MetadataCodec.ForStore(store), discord.DownloadAttachmentBytesAsync);
        SharedStorage = new SharedStorageManager(discord, store, settings, journalIndex: GetStorageJournalIndexAsync);
        _serials = new SerialIndexService(discord, store, settings);
        _mappings = new ConcurrentDictionary<string, FolderIndexMapping>(
            store.LoadFolderIndexMappings(),
            StringComparer.OrdinalIgnoreCase);
        _descriptors[RootFolderId] = new FolderDescriptor(RootFolderId, RootFolderId, RootFolderId, "/");
        _descriptors[TrashFolderId] = new FolderDescriptor(TrashFolderId, RootFolderId, TrashFolderId, TrashPath);
    }

    public async Task<List<AppEntry>> LoadCachedFolderAsync(
        string folderId,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedFolderId = NormalizeFolderId(folderId);
        var cached = _store.LoadCachedEntriesForFolder(normalizedFolderId);
        var serial = await _serials.LoadCachedFolderAsync(normalizedFolderId, cancellationToken);
        if (serial == null && _mappings.TryGetValue(normalizedFolderId, out var mapping))
        {
            serial = await _serials.LoadFolderAsync(mapping, cancellationToken);
        }
        var entries = serial == null
            ? cached
            : _serials.MaterializeEntries(serial, cached, folderPath, MappingSnapshot()).ToList();
        NormalizeFolderEntries(entries, normalizedFolderId, folderPath);
        RecordDescriptors(entries);
        return entries;
    }

    public async Task<List<AppEntry>> LoadFavoritesAsync(
        CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        var serial = await _serials.LoadStandaloneSerialAsync(
            SerialIndexService.FavoritesSerialId,
            FolderSerialKind.Favorites,
            cancellationToken,
            forceRefresh);
        if (serial == null)
        {
            return [];
        }

        if (_mappings.TryGetValue(SerialIndexService.FavoritesSerialId, out var mapping))
        {
            var cachedHash = await _serials.CachedSerialSha256Async(
                SerialIndexService.FavoritesSerialId, cancellationToken);
            if (cachedHash == null ||
                !cachedHash.Equals(mapping.SerialSha256, StringComparison.OrdinalIgnoreCase))
            {
                await PublishFavoritesSerialAsync(serial, cancellationToken);
            }
        }

        var entries = _serials.MaterializeEntries(
                serial,
                [],
                "/",
                MappingSnapshot(),
                preserveChildParentFolderId: true)
            .ToList();
        foreach (var entry in entries)
        {
            entry.Path = ResolveKnownFolderPath(entry.ParentFolderId);
        }
        var trashedFolders = await LoadTrashFolderIdsAsync(cancellationToken);
        return entries.Where(entry => !trashedFolders.Contains(NormalizeFolderId(entry.ParentFolderId))).ToList();
    }

    public async Task<List<AppEntry>> LoadSynchronizedDevicesAsync(
        CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        if (!forceRefresh &&
            await _serials.LoadCachedFolderAsync(SynchronizedRootFolderId, cancellationToken) is
                { Kind: FolderSerialKind.Folder, Children.Count: > 0 })
        {
            return await LoadSynchronizedDevicesFromSerialAsync(cancellationToken);
        }

        try
        {
            var metadataEntries = await ReadStandaloneMetadataEntriesAsync(cancellationToken);
            var rebuilt = await RebuildSynchronizedSerialsAsync(metadataEntries, cancellationToken);
            if (rebuilt != null)
            {
                return rebuilt;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MetadataCodecException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Error("Registry.RefreshSynchronized", ex);
        }

        return await LoadSynchronizedDevicesFromSerialAsync(cancellationToken, forceRefresh);
    }

    public async Task RefreshServerIndexAsync(
        bool rebuildSerials,
        CancellationToken cancellationToken = default,
        Func<string, IReadOnlyList<AppEntry>, Task>? onFolderRefreshed = null)
    {
        await _serverIndexRefreshGate.WaitAsync(cancellationToken);
        try
        {
            await RefreshMappingsAsync(cancellationToken);
            if (rebuildSerials)
            {
                await _serials.ResetAllAsync(cancellationToken);
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await RefreshMappedTreeAsync(
                RootFolderId,
                "/",
                visited,
                cancellationToken,
                onFolderRefreshed);

            if (_mappings.ContainsKey(SynchronizedRootFolderId) && !visited.Contains(SynchronizedRootFolderId))
            {
                await RefreshMappedTreeAsync(SynchronizedRootFolderId, SynchronizedRootPath, visited, cancellationToken, onFolderRefreshed);
            }

            if (_mappings.ContainsKey(TrashFolderId))
            {
                await RefreshMappedTreeAsync(TrashFolderId, TrashPath, visited, cancellationToken, onFolderRefreshed);
            }

            if (rebuildSerials)
            {
                var metadataEntries = await ReadStandaloneMetadataEntriesAsync(cancellationToken);
                await RebuildSynchronizedSerialsAsync(metadataEntries, cancellationToken);
                await RebuildFavoritesSerialAsync(metadataEntries, cancellationToken);
            }
        }
        finally
        {
            _serverIndexRefreshGate.Release();
        }
    }

    public async Task<AppEntry> LoadFileDetailsAsync(AppEntry entry, CancellationToken cancellationToken = default,
        bool forceRefresh = false, bool cacheDetails = true)
    {
        if (entry.Type != "file" || (!forceRefresh && entry.LogicalChunkCount > 0) ||
            string.IsNullOrWhiteSpace(entry.MetadataMessageId))
        {
            return entry;
        }

        if (!forceRefresh && !string.IsNullOrEmpty(entry.Encryption?.Salt))
        {
            var known = _store.LoadCachedFileEntry(NormalizeFolderId(entry.ParentFolderId), entry.MetadataMessageId);
            if (known != null && known.FileId == entry.FileId && known.ThreadId == entry.ThreadId &&
                known.Encryption?.Salt == entry.Encryption.Salt && known.PreviewMessageId == entry.PreviewMessageId &&
                known.LogicalChunkCount > 0)
            { known.Path = entry.Path; return known; }
        }
        var channels = new List<string>();
        if (_mappings.TryGetValue(NormalizeFolderId(entry.ParentFolderId), out var mapping) &&
            !channels.Contains(mapping.ThreadId, StringComparer.Ordinal))
        {
            channels.Add(mapping.ThreadId);
        }
        if (!channels.Contains(_settings.NamesChannelId, StringComparer.Ordinal)) channels.Add(_settings.NamesChannelId);

        foreach (var channelId in channels.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            try
            {
                var message = await _discord.GetMessageAsync(channelId, entry.MetadataMessageId, cancellationToken);
                var envelope = await _metadata.ReadEnvelopeAsync(message, cancellationToken);
                var detailed = envelope == null ? null : _metadata.ToAppEntry(envelope);
                if (detailed?.Type == "file" &&
                    detailed.FileId.Equals(entry.FileId, StringComparison.OrdinalIgnoreCase) &&
                    detailed.ThreadId.Equals(entry.ThreadId, StringComparison.Ordinal))
                {
                    detailed.Path = entry.Path;
                    detailed.ParentFolderId = entry.ParentFolderId;
                    if (cacheDetails)
                    {
                        _store.CacheFileDetails(detailed);
                    }
                    return detailed;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MetadataCodecException) { throw; }
            catch (DiscordApiException ex) when ((int)ex.StatusCode == 404) { }
            catch (Exception ex)
            {
                AppLogger.Error("Registry.LoadFileDetails", ex, entry.MetadataMessageId);
            }
        }

        if (forceRefresh)
            throw new InvalidDataException("Não foi possível ler os metadados atuais do arquivo no servidor.");
        return entry;
    }

    internal async Task<List<AppEntry>> LoadSynchronizedDevicesFromSerialAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false,
        bool allowMetadataFallback = true)
    {
        var serial = await _serials.LoadStandaloneSerialAsync(
            SynchronizedRootFolderId,
            FolderSerialKind.Folder,
            cancellationToken,
            forceRefresh);
        if (serial == null && _mappings.TryGetValue(SynchronizedRootFolderId, out var mapping))
        {
            serial = await _serials.LoadFolderAsync(mapping, cancellationToken);
        }

        if (serial == null)
        {
            if (!allowMetadataFallback)
                return _store.LoadCachedEntriesForFolder(SynchronizedRootFolderId).Where(entry => entry.Type == "folder").ToList();
            if (!_mappings.TryGetValue(SynchronizedRootFolderId, out var folderMapping))
            {
                return [];
            }

            var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
            var messages = await _discord.GetMessagesInParallelAsync(
                folderMapping.ThreadId, workers, cancellationToken);
            var envelopes = await _metadata.ReadEnvelopesInParallelAsync(messages, workers, cancellationToken);
            return _metadata.ToFolderEntries(
                    envelopes, SynchronizedRootFolderId, SynchronizedRootPath, MappingSnapshot())
                .Where(entry => entry.Type == "folder")
                .ToList();
        }

        return _serials.MaterializeEntries(
                serial,
                _store.LoadCachedEntriesForFolder(SynchronizedRootFolderId),
                SynchronizedRootPath,
                MappingSnapshot())
            .Where(entry => entry.Type == "folder")
            .ToList();
    }

    private async Task<IReadOnlyList<AppEntry>> ReadStandaloneMetadataEntriesAsync(
        CancellationToken cancellationToken)
    {
        var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
        var messages = await _discord.GetMessagesInParallelAsync(
            _settings.NamesChannelId,
            workers,
            cancellationToken);
        var envelopes = await _metadata.ReadEnvelopesInParallelAsync(messages, workers, cancellationToken);
        return envelopes
            .Select(_metadata.ToAppEntry)
            .Where(entry => entry != null)
            .Cast<AppEntry>()
            .GroupBy(EntryIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => Snowflake(entry.MetadataMessageId))
                .First())
            .ToList();
    }

    private async Task<List<AppEntry>?> RebuildSynchronizedSerialsAsync(
        IReadOnlyList<AppEntry> metadataEntries,
        CancellationToken cancellationToken)
    {
        var directChildrenExist = metadataEntries.Any(entry =>
            NormalizeFolderId(entry.ParentFolderId)
                .Equals(SynchronizedRootFolderId, StringComparison.OrdinalIgnoreCase));
        if (!directChildrenExist)
        {
            return null;
        }

        var byParent = metadataEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ParentFolderId))
            .GroupBy(entry => NormalizeFolderId(entry.ParentFolderId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var foldersById = metadataEntries
            .Where(entry => entry.Type == "folder" && !string.IsNullOrWhiteSpace(entry.FolderId))
            .GroupBy(entry => entry.FolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string FolderId, string Path)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((SynchronizedRootFolderId, SynchronizedRootPath));
        List<AppEntry> devices = [];

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            if (!visited.Add(current.FolderId))
            {
                continue;
            }

            var children = byParent.TryGetValue(current.FolderId, out var found)
                ? found
                : [];
            if (_mappings.TryGetValue(current.FolderId, out var indexed))
            {
                var indexedMessages = await _discord.GetMessagesInParallelAsync(indexed.ThreadId, _settings.SearchWorkers, cancellationToken);
                var indexedMetadata = await _metadata.ReadEnvelopesInParallelAsync(indexedMessages, _settings.SearchWorkers, cancellationToken);
                children = children.Concat(_metadata.ToFolderEntries(indexedMetadata, current.FolderId, current.Path, MappingSnapshot()))
                    .GroupBy(EntryIdentity, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToList();
            }
            foreach (var child in children)
            {
                child.Path = current.Path;
                child.ParentFolderId = current.FolderId;
                child.Normalize();
            }

            _store.CacheFolderEntries(current.FolderId, children);
            RecordDescriptors(children);
            var descriptor = foldersById.GetValueOrDefault(current.FolderId);
            var parentFolderId = descriptor == null
                ? RootFolderId
                : NormalizeFolderId(descriptor.ParentFolderId);
            var folderName = descriptor?.Name ?? current.FolderId;
            var serial = _serials.BuildFolderSerial(
                current.FolderId,
                parentFolderId,
                folderName,
                children,
                MappingSnapshot());
            if (_mappings.ContainsKey(current.FolderId))
            {
                await SynchronizeFolderSerialAsync(current.FolderId, current.Path, children, cancellationToken);
            }
            else
            {
                await _serials.SaveStandaloneSerialAsync(serial, cancellationToken);
            }

            if (current.FolderId.Equals(SynchronizedRootFolderId, StringComparison.OrdinalIgnoreCase))
            {
                devices = children.Where(entry => entry.Type == "folder").ToList();
            }

            foreach (var folder in children.Where(entry =>
                         entry.Type == "folder" && !string.IsNullOrWhiteSpace(entry.FolderId)))
            {
                queue.Enqueue((folder.FolderId, PathRules.ChildPath(folder)));
            }
        }

        return devices;
    }

    private async Task RefreshMappedTreeAsync(
        string folderId,
        string folderPath,
        HashSet<string> visited,
        CancellationToken cancellationToken,
        Func<string, IReadOnlyList<AppEntry>, Task>? onFolderRefreshed = null)
    {
        if (!visited.Add(folderId))
        {
            return;
        }

        var entries = await RefreshFolderAsync(folderId, folderPath, cancellationToken);
        if (onFolderRefreshed != null)
        {
            await onFolderRefreshed(folderId, entries);
        }
        foreach (var folder in entries.Where(entry =>
                     entry.Type == "folder" &&
                     !string.IsNullOrWhiteSpace(entry.FolderId) &&
                     _mappings.ContainsKey(entry.FolderId)))
        {
            await RefreshMappedTreeAsync(
                folder.FolderId,
                PathRules.ChildPath(folder),
                visited,
                cancellationToken,
                onFolderRefreshed);
        }
    }

    public async Task SetFavoriteAsync(
        AppEntry entry,
        bool favorite,
        CancellationToken cancellationToken = default)
    {
        var parentFolderId = NormalizeFolderId(entry.ParentFolderId);
        if (string.IsNullOrWhiteSpace(entry.MetadataMessageId))
        {
            throw new InvalidOperationException("O item não possui mensagem de metadados.");
        }

        MetadataEnvelope? envelope = null;
        try
        {
            var message = await _discord.GetMessageAsync(
                _settings.NamesChannelId,
                entry.MetadataMessageId,
                cancellationToken);
            envelope = await _metadata.ReadEnvelopeAsync(message, cancellationToken);
        }
        catch (DiscordApiException ex) when ((int)ex.StatusCode == 404)
        {
            // Desktop-native entries keep metadata in the parent folder index.
        }

        var authoritative = envelope == null ? null : _metadata.ToAppEntry(envelope);
        if (authoritative != null && SameEntryIdentity(authoritative, entry))
        {
            authoritative.Path = entry.Path;
            authoritative.Favorite = favorite;
            await _metadata.UpsertEntryAsync(
                _discord,
                _settings.NamesChannelId,
                authoritative,
                cancellationToken);
            CopyEntryState(authoritative, entry);
            await UpdateStandaloneFavoriteAsync(entry, cancellationToken);
            return;
        }

        if (_mappings.ContainsKey(parentFolderId))
        {
            if (entry.Type == "file")
            {
                var detailed = await LoadFileDetailsAsync(entry, cancellationToken, forceRefresh: true);
                CopyEntryState(detailed, entry);
            }
            entry.Favorite = favorite;
            await UpdateEntryAsync(entry, cancellationToken);
            return;
        }

        throw new InvalidOperationException("Os metadados do item não puderam ser carregados.");
    }

    private async Task UpdateStandaloneFavoriteAsync(AppEntry entry, CancellationToken cancellationToken)
    {
        var parentFolderId = NormalizeFolderId(entry.ParentFolderId);
        var parent = await _serials.LoadStandaloneSerialAsync(
            parentFolderId,
            FolderSerialKind.Folder,
            cancellationToken);
        if (parent != null)
        {
            var id = entry.Type == "folder" ? entry.FolderId : entry.FileId;
            var entryFound = false;
            var children = parent.Children
                .Select(child =>
                {
                    if (!child.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    {
                        return child;
                    }

                    entryFound = true;
                    return CopySerialEntry(child, entry);
                })
                .ToList();
            if (!entryFound)
            {
                children.Add(_serials.BuildFolderSerial(
                    parentFolderId,
                    parent.FolderName,
                    parent.ParentFolderId,
                    [entry],
                    MappingSnapshot()).Children.Single());
            }
            if (_mappings.ContainsKey(parentFolderId))
            {
                var path = ResolveKnownFolderPath(parentFolderId);
                var updatedEntries = _serials.MaterializeEntries(
                    CopySerial(parent, children),
                    _store.LoadCachedEntriesForFolder(parentFolderId),
                    path,
                    MappingSnapshot()).ToList();
                await SynchronizeFolderSerialAsync(parentFolderId, path, updatedEntries, cancellationToken);
            }
            else
            {
                await _serials.SaveStandaloneSerialAsync(CopySerial(parent, children), cancellationToken);
            }
        }

        var favorites = await _serials.LoadStandaloneSerialAsync(
            SerialIndexService.FavoritesSerialId,
            FolderSerialKind.Favorites,
            cancellationToken);
        var current = favorites?.Children.ToList() ?? [];
        var entryId = entry.Type == "folder" ? entry.FolderId : entry.FileId;
        current.RemoveAll(child => child.Id.Equals(entryId, StringComparison.OrdinalIgnoreCase));
        if (entry.Favorite)
        {
            var single = _serials.BuildFolderSerial(
                parentFolderId,
                parentFolderId,
                parentFolderId,
                [entry],
                MappingSnapshot());
            current.Add(single.Children.Single());
        }

        var materialized = current.Select(child => new AppEntry
        {
            Name = child.Name,
            ThreadId = child.ThreadId,
            UploadedAt = child.UploadedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            MetadataMessageId = child.NamesMessageId,
            FileId = child.IsFolder ? "" : child.Id,
            FolderId = child.IsFolder ? child.Id : "",
            ParentFolderId = child.ParentFolderId,
            FolderColor = child.FolderColor,
            DeviceType = child.DeviceType,
            Favorite = true,
            Type = child.IsFolder ? "folder" : "file",
            MimeType = child.MimeType,
            PreviewMessageId = child.PreviewMessageId,
            Encryption = child.Encryption?.Clone()
        }).ToList();
        var built = _serials.BuildFolderSerial(
            SerialIndexService.FavoritesSerialId,
            RootFolderId,
            SerialIndexService.FavoritesSerialId,
            materialized,
            MappingSnapshot());
        await PublishFavoritesSerialAsync(new FolderSerial
        {
            FolderId = built.FolderId,
            ParentFolderId = built.ParentFolderId,
            FolderName = built.FolderName,
            GeneratedAt = built.GeneratedAt,
            Aggregate = built.Aggregate,
            Children = built.Children,
            Kind = FolderSerialKind.Favorites
        }, cancellationToken);
    }

    public Task<List<AppEntry>> RefreshFolderAsync(
        string folderId,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedFolderId = NormalizeFolderId(folderId);
        return _folderRefreshes.GetOrAdd(
            normalizedFolderId,
            _ => RefreshFolderCoreAsync(normalizedFolderId, folderPath, cancellationToken));
    }

    public async Task<string?> ResolveFolderIdByPathAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = PathRules.Normalize(folderPath);
        if (normalizedPath == "/")
        {
            return RootFolderId;
        }

        var currentFolderId = RootFolderId;
        var currentPath = "/";
        foreach (var segment in normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var entries = await LoadCachedFolderAsync(currentFolderId, currentPath, cancellationToken);
            var folder = entries
                .Where(entry => entry.Type == "folder" &&
                                entry.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.MetadataMessageId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (folder == null || string.IsNullOrWhiteSpace(folder.FolderId))
            {
                entries = await RefreshFolderAsync(currentFolderId, currentPath, cancellationToken);
                folder = entries
                    .Where(entry => entry.Type == "folder" &&
                                    entry.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.MetadataMessageId, StringComparer.Ordinal)
                    .FirstOrDefault();
            }

            if (folder == null || string.IsNullOrWhiteSpace(folder.FolderId))
            {
                return null;
            }

            currentFolderId = folder.FolderId;
            currentPath = PathRules.ChildPath(folder);
        }

        return currentFolderId;
    }

    public async Task<long?> GetFolderFileCountAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        var normalizedFolderId = NormalizeFolderId(folderId);
        if (_folderFileCounts.TryGetValue(normalizedFolderId, out var cached))
        {
            return cached;
        }

        var count = await CountFolderFilesAsync(
            normalizedFolderId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        if (count.HasValue)
        {
            _folderFileCounts[normalizedFolderId] = count.Value;
        }

        return count;
    }

    public async Task<LibraryStatistics?> GetCachedLibraryStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadLibraryTotalsAsync(RootFolderId, cancellationToken, cachedOnly: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLogger.Error("Registry.CachedStatistics", ex); return null; }
    }

    public async Task<LibraryStatistics?> GetLibraryStatisticsAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        var normalizedFolderId = NormalizeFolderId(folderId);
        if (_libraryStatistics.TryGetValue(normalizedFolderId, out var cached))
        {
            return cached;
        }

        var statistics = await ReadLibraryTotalsAsync(normalizedFolderId, cancellationToken);
        if (statistics != null)
        {
            _folderFileCounts[normalizedFolderId] = statistics.FileCount;
            _libraryStatistics[normalizedFolderId] = statistics;
        }

        return statistics;
    }

    public async Task RefreshMappingsAsync(CancellationToken cancellationToken = default)
    {
        await _mappingRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
            var messages = await _discord.GetMessagesInParallelAsync(
                _settings.NamesChannelId,
                workers,
                cancellationToken);
            var mappings = await _metadata.ReadFolderIndexMappingsInParallelAsync(messages, workers, cancellationToken);
            var canonical = mappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.FolderId) && !string.IsNullOrWhiteSpace(mapping.ThreadId))
                .GroupBy(mapping => mapping.FolderId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(mapping => Snowflake(mapping.NamesMessageId))
                    .First())
                .ToList();

            if (canonical.Count == 0 && !_mappings.IsEmpty)
            {
                throw new InvalidOperationException("A leitura dos mappings retornou vazia; o índice local foi preservado.");
            }
            foreach (var mapping in canonical)
            {
                if (_mappings.TryGetValue(mapping.FolderId, out var previous) &&
                    (previous.SerialMessageId != mapping.SerialMessageId || previous.SerialSha256 != mapping.SerialSha256))
                    await _serials.InvalidateCachedFolderAsync(mapping.FolderId);
                _mappings[mapping.FolderId] = mapping;
            }
            var currentIds = canonical.Select(mapping => mapping.FolderId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folderId in _mappings.Keys.Where(folderId => !currentIds.Contains(folderId)))
            {
                _mappings.TryRemove(folderId, out _);
            }
            _store.ReplaceFolderIndexMappings(canonical);
            _folderFileCounts.Clear();
            _libraryStatistics.Clear();
        }
        finally
        {
            _mappingRefreshGate.Release();
        }
    }

    public async Task<int> ArchiveActiveFolderThreadsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DiscordChannelDto> active;
        try
        {
            active = await _discord.GetActiveThreadsAsync(_settings.FoldersForumId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Registry.ArchiveActiveFolderThreads", ex, _settings.FoldersForumId);
            return 0;
        }

        var targets = active
            .Where(thread => !string.IsNullOrWhiteSpace(thread.Id))
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        var archived = 0;
        var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = workers
            },
            async (thread, token) =>
            {
                if (await ArchiveFolderThreadAsync(thread.Id, token))
                {
                    Interlocked.Increment(ref archived);
                }
            });
        return archived;
    }

    public async Task<List<AppEntry>> LoadFolderTreeAsync(
        AppEntry folder,
        bool allowOrphanThread = false,
        CancellationToken cancellationToken = default)
    {
        if (folder.Type != "folder" || string.IsNullOrWhiteSpace(folder.FolderId))
        {
            return [];
        }

        var result = new List<AppEntry>();
        async Task VisitAsync(AppEntry current)
        {
            var path = PathRules.ChildPath(current);
            var children = await LoadFolderTreeChildrenAsync(current, path, allowOrphanThread, cancellationToken);
            foreach (var child in children)
            {
                result.Add(child);
            }

            foreach (var child in children.Where(child => child.Type == "folder" && !string.IsNullOrWhiteSpace(child.FolderId)))
            {
                await VisitAsync(child);
            }
        }

        await VisitAsync(folder);
        return result;
    }

    private async Task<List<AppEntry>> LoadFolderTreeChildrenAsync(
        AppEntry folder,
        string folderPath,
        bool allowOrphanThread,
        CancellationToken cancellationToken)
    {
        try
        {
            await GetMappingAsync(folder.FolderId, cancellationToken);
            return await RefreshFolderAsync(folder.FolderId, folderPath, cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            allowOrphanThread &&
            TryGetFolderIndexThreadId(folder, out var threadId))
        {
            // A folder metadata still carries its index thread when its names mapping was lost.
            // Use that thread only for deletion so the visible orphan can be removed safely.
            AppLogger.Error("Registry.DeleteOrphanFolder", ex, folder.FolderId);
            var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
            var messages = await _discord.GetMessagesInParallelAsync(threadId, workers, cancellationToken);
            var envelopes = await _metadata.ReadEnvelopesInParallelAsync(messages, workers, cancellationToken);
            var entries = _metadata.ToFolderEntries(envelopes, folder.FolderId, folderPath, MappingSnapshot());
            NormalizeFolderEntries(entries, folder.FolderId, folderPath);
            RecordDescriptors(entries);
            _store.CacheFolderEntries(folder.FolderId, entries);
            return entries;
        }
    }

    public Task<string> AddEntryAsync(
        AppEntry entry,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool synchronizeSerials = true) =>
        PublishEntryAsync(entry, cancellationToken, onRateLimitWait, synchronizeSerials, newUploadIdentity: false);

    internal Task<string> AddNewUploadEntryAsync(AppEntry entry, CancellationToken token,
        Func<TimeSpan, Task>? onRateLimitWait)
    {
        if (entry.Type != "file" || entry.Storage?.Shared != true ||
            !Guid.TryParseExact(entry.FileId, "N", out _) || !Guid.TryParseExact(entry.VersionId, "N", out _) ||
            !string.IsNullOrEmpty(entry.MetadataMessageId) || entry.Versions.Count > 0 || Volatile.Read(ref _uploadIndexBatches) == 0)
            throw new InvalidOperationException("A publicação inicial exige uma identidade nova reservada pelo lote.");
        return PublishEntryAsync(entry, token, onRateLimitWait, synchronizeSerials: false, newUploadIdentity: true);
    }

    private async Task<string> PublishEntryAsync(AppEntry entry, CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait, bool synchronizeSerials, bool newUploadIdentity)
    {
        await using var priority = SharedStorage.PrioritizeMutations();
        // A freshly generated, already reserved identity only creates a new message.
        // Existing identities, versions and resumptions still acquire the remote edit claim.
        await using var mutation = newUploadIdentity ? null : await SharedStorage.AcquireMetadataMutationAsync(entry, cancellationToken);
        string messageId;
        if (entry.Type == "file" && !synchronizeSerials && Volatile.Read(ref _uploadIndexBatches) > 0)
        {
            messageId = await AddUploadFileEntryAsync(entry, cancellationToken, onRateLimitWait);
        }
        else
        {
            await _entryMutationGate.WaitAsync(cancellationToken);
            try { messageId = await AddEntryCoreAsync(entry, cancellationToken, onRateLimitWait, synchronizeSerials); }
            finally { _entryMutationGate.Release(); }
        }
        if (entry.Type == "file")
            await SharedStorage.PublishFileReferenceAsync(entry, (await GetMappingAsync(NormalizeFolderId(entry.ParentFolderId), cancellationToken)).ThreadId, cancellationToken);
        return messageId;
    }

    public async Task SynchronizeFolderSerialsAsync(
        IEnumerable<string?> folderIds,
        CancellationToken cancellationToken = default)
    {
        var distinctFolderIds = folderIds
            .Select(NormalizeFolderId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var folderId in distinctFolderIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_mappings.ContainsKey(folderId))
            {
                _deferredFolderSerials.TryRemove(folderId, out _);
                continue;
            }

            try
            {
                var descriptor = ResolveFolderDescriptor(folderId, "/");
                var entries = _store.LoadCachedEntriesForFolder(folderId);
                await SynchronizeFolderSerialAsync(folderId, descriptor.Path, entries, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MetadataCodecException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Error("Registry.BatchSerial", ex, folderId);
            }
            finally
            {
                _deferredFolderSerials.TryRemove(folderId, out _);
            }
        }
    }

    public async Task CleanupSupersededRemoteSerialsAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _remoteSerialCleanupStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await SharedStorage.ReconcileAsync(cancellationToken);
            await _serials.CleanupSupersededRemoteSerialsAsync(
                MappingSnapshot(),
                _settings.SearchWorkers,
                DeleteSupersededSerialIfCurrentAsync,
                cancellationToken);
        }
        catch
        {
            Volatile.Write(ref _remoteSerialCleanupStarted, 0);
            throw;
        }
    }

    private async Task<string> AddEntryCoreAsync(
        AppEntry entry,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait,
        bool synchronizeSerials)
    {
        entry.Normalize();
        var parentFolderId = NormalizeFolderId(entry.ParentFolderId);
        entry.ParentFolderId = parentFolderId;
        var parent = await GetMappingAsync(parentFolderId, cancellationToken);
        await using var parentWrite = !synchronizeSerials && _uploadIndexBatches > 0
            ? null : await _discord.AcquireThreadWriteAsync(parent.ThreadId, cancellationToken);
        await OpenEntryIndexAsync(parent.ThreadId, synchronizeSerials, cancellationToken, onRateLimitWait);
        if (entry.Type == "folder")
        {
            var existing = await FindExistingFolderAsync(entry, parentFolderId, cancellationToken);
            if (existing != null)
            {
                var existingMapping = await GetMappingAsync(existing.FolderId, cancellationToken);
                entry.FolderId = existing.FolderId;
                entry.ThreadId = existingMapping.ThreadId;
                entry.MetadataMessageId = existing.MetadataMessageId;
                entry.UploadedAt = existing.UploadedAt;
                entry.Path = existing.Path;
                entry.ParentFolderId = existing.ParentFolderId;
                entry.FolderColor = existing.FolderColor;
                return entry.MetadataMessageId;
            }

            var created = await EnsureFolderIndexAsync(entry, cancellationToken, synchronizeSerials);
            entry.ThreadId = created.ThreadId;
        }

        try
        {
            await _metadata.SendEntryAsync(_discord, parent.ThreadId, entry, cancellationToken, onRateLimitWait);
            var siblings = _store.LoadCachedEntriesForFolder(parentFolderId);
            ReplaceEntry(siblings, entry);
            _store.CacheFolderEntries(parentFolderId, siblings);
            RecordDescriptors(siblings);
            if (synchronizeSerials)
            {
                await SynchronizeFolderSerialAsync(parentFolderId, entry.Path, siblings, cancellationToken);
                if (entry.Type == "folder" && string.IsNullOrWhiteSpace(_mappings[entry.FolderId].SerialMessageId))
                {
                    await SynchronizeFolderSerialAsync(entry.FolderId, PathRules.ChildPath(entry), [], cancellationToken);
                }
            }
        }
        finally
        {
            await ArchiveFolderThreadAsync(parent.ThreadId, cancellationToken);
        }

        return entry.MetadataMessageId;
    }

    private async Task<AppEntry?> FindExistingFolderAsync(
        AppEntry folder,
        string parentFolderId,
        CancellationToken cancellationToken)
    {
        var cached = _store.LoadCachedEntriesForFolder(parentFolderId);
        var cachedExisting = cached.FirstOrDefault(candidate => IsSameFolderIdentity(candidate, folder));
        if (cachedExisting != null || _deferredFolderSerials.ContainsKey(parentFolderId))
        {
            return cachedExisting;
        }

        var siblings = await RefreshFolderAsync(parentFolderId, folder.Path, cancellationToken);
        return siblings.FirstOrDefault(candidate => IsSameFolderIdentity(candidate, folder));
    }

    public async Task UpdateEntryAsync(
        AppEntry entry,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        FileMetadata? expectedPayload = null)
    {
        await using var priority = SharedStorage.PrioritizeMutations();
        if (entry.Type == "file" && entry.LogicalChunkCount == 0)
        {
            var details = await LoadFileDetailsAsync(entry, cancellationToken);
            CopyFilePayload(details, entry);
        }
        entry.Normalize();
        await using var mutation = await SharedStorage.AcquireMetadataMutationAsync(entry, cancellationToken);
        if (entry.Type == "file" && (entry.Storage?.Shared == true || expectedPayload?.Storage?.Shared == true))
        {
            var basis = expectedPayload == null ? entry : _metadata.ToAppEntry(new MetadataEnvelope(entry.MetadataMessageId, expectedPayload))!;
            basis.Path = entry.Path;
            var current = await LoadFileDetailsAsync(basis, cancellationToken, forceRefresh: true);
            if (expectedPayload == null) CopyFilePayload(current, entry);
            else
            {
                if (current.VersionId != (expectedPayload.VersionId ?? "") || current.BatchId != expectedPayload.BatchId)
                    throw new IOException("O conteúdo remoto mudou antes da publicação. As versões existentes foram preservadas.");
                entry.Favorite = current.Favorite;
                if (entry.Sync?.RevisionId == current.Sync?.RevisionId)
                { entry.Name = current.Name; entry.Trash = current.Trash; entry.Sync = current.Sync; }
            }
        }
        var parentFolderId = NormalizeFolderId(entry.ParentFolderId);
        entry.ParentFolderId = parentFolderId;
        var parent = await GetMappingAsync(parentFolderId, cancellationToken);
        await _discord.UnarchiveThreadAsync(parent.ThreadId, cancellationToken, onRateLimitWait);
        try
        {
            await _metadata.UpsertEntryAsync(_discord, parent.ThreadId, entry, cancellationToken, onRateLimitWait);
            var siblings = _store.LoadCachedEntriesForFolder(parentFolderId);
            ReplaceEntry(siblings, entry);
            _store.CacheFolderEntries(parentFolderId, siblings);
            RecordDescriptors(siblings);
            await SynchronizeFolderSerialAsync(parentFolderId, entry.Path, siblings, cancellationToken);

            if (entry.Type == "folder")
            {
                var mapping = await GetMappingAsync(entry.FolderId, cancellationToken);
                await _discord.UnarchiveThreadAsync(mapping.ThreadId, cancellationToken, onRateLimitWait);
                try
                {
                    await _discord.RenameThreadAsync(mapping.ThreadId, FolderThreadTitle(entry.Name, entry.FolderId), cancellationToken, onRateLimitWait);
                    var children = _store.LoadCachedEntriesForFolder(entry.FolderId);
                    await SynchronizeFolderSerialAsync(entry.FolderId, PathRules.ChildPath(entry), children, cancellationToken);
                }
                finally
                {
                    await ArchiveFolderThreadAsync(mapping.ThreadId, cancellationToken);
                }
            }
        }
        finally
        {
            await ArchiveFolderThreadAsync(parent.ThreadId, cancellationToken);
        }

        if (entry.Type == "file")
            await SharedStorage.PublishFileReferenceAsync(entry, parent.ThreadId, cancellationToken);
        await RebuildFavoritesSerialAsync(cancellationToken);
    }

    public async Task MoveEntryAsync(
        AppEntry entry,
        string targetFolderId,
        string targetPath,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool synchronizeSerials = true)
    {
        if (entry.Type == "file")
        {
            var details = await LoadFileDetailsAsync(entry, cancellationToken, forceRefresh: true);
            CopyFilePayload(details, entry);
        }
        entry.ModifiedAt = TransferState.Now();
        entry.Normalize();
        var sourceFolderId = NormalizeFolderId(entry.ParentFolderId);
        var destinationFolderId = NormalizeFolderId(targetFolderId);
        if (sourceFolderId.Equals(destinationFolderId, StringComparison.OrdinalIgnoreCase))
        {
            entry.Path = PathRules.Normalize(targetPath);
            await UpdateEntryAsync(entry, cancellationToken, onRateLimitWait);
            return;
        }
        await using var mutation = await SharedStorage.AcquireMetadataMutationAsync(entry, cancellationToken);
        if (entry.Type == "file" && entry.Storage?.Shared == true)
            CopyFilePayload(await LoadFileDetailsAsync(entry, cancellationToken, forceRefresh: true), entry);

        var source = await GetMappingAsync(sourceFolderId, cancellationToken);
        await _discord.UnarchiveThreadAsync(source.ThreadId, cancellationToken, onRateLimitWait);
        try
        {
            await _metadata.DeleteEntryAsync(_discord, source.ThreadId, entry, cancellationToken);
            var sourceChildren = _store.LoadCachedEntriesForFolder(sourceFolderId);
            RemoveEntry(sourceChildren, entry);
            _store.CacheFolderEntries(sourceFolderId, sourceChildren);
            if (synchronizeSerials)
            {
                await SynchronizeFolderSerialAsync(sourceFolderId, entry.Path, sourceChildren, cancellationToken);
            }
        }
        finally
        {
            await ArchiveFolderThreadAsync(source.ThreadId, cancellationToken);
        }

        entry.ParentFolderId = destinationFolderId;
        entry.Path = PathRules.Normalize(targetPath);
        entry.MetadataMessageId = "";
        var destination = await GetMappingAsync(destinationFolderId, cancellationToken);
        await _discord.UnarchiveThreadAsync(destination.ThreadId, cancellationToken, onRateLimitWait);
        try
        {
            await _metadata.SendEntryAsync(_discord, destination.ThreadId, entry, cancellationToken, onRateLimitWait);
            var destinationChildren = _store.LoadCachedEntriesForFolder(destinationFolderId);
            ReplaceEntry(destinationChildren, entry);
            _store.CacheFolderEntries(destinationFolderId, destinationChildren);
            RecordDescriptors(destinationChildren);
            if (synchronizeSerials)
            {
                await SynchronizeFolderSerialAsync(destinationFolderId, entry.Path, destinationChildren, cancellationToken);
            }
        }
        finally
        {
            await ArchiveFolderThreadAsync(destination.ThreadId, cancellationToken);
        }

        if (entry.Type == "file")
            await SharedStorage.PublishFileReferenceAsync(entry, destination.ThreadId, cancellationToken);
        if (synchronizeSerials && entry.Type == "folder")
        {
            var children = _store.LoadCachedEntriesForFolder(entry.FolderId);
            await SynchronizeFolderSerialAsync(entry.FolderId, PathRules.ChildPath(entry), children, cancellationToken);
        }

        if (synchronizeSerials)
        {
            await RebuildFavoritesSerialAsync(cancellationToken);
        }
    }

    public async Task DeleteEntryAsync(
        AppEntry entry,
        CancellationToken cancellationToken = default,
        bool synchronizeSerials = true)
    {
        await using var priority = SharedStorage.PrioritizeMutations();
        var folderMapping = entry.Type == "folder" && !string.IsNullOrWhiteSpace(entry.FolderId)
            ? await GetFolderMappingForDeletionAsync(entry.FolderId, entry, cancellationToken)
            : null;

        var parentFolderId = NormalizeFolderId(entry.ParentFolderId);
        var parent = await GetFolderMappingForDeletionAsync(parentFolderId, null, cancellationToken);
        var metadataChannel = entry.Type == "file" ? await SharedStorage.LocateMetadataAsync(entry, cancellationToken) : parent.ThreadId;
        await using (await _discord.AcquireThreadWriteAsync(parent.ThreadId, cancellationToken))
        {
            await _metadata.DeleteEntryAsync(_discord, metadataChannel, entry, cancellationToken);
            List<AppEntry> siblings;
            await _entryMutationGate.WaitAsync(cancellationToken);
            try
            {
                siblings = _store.LoadCachedEntriesForFolder(parentFolderId);
                RemoveEntry(siblings, entry);
                _store.CacheFolderEntries(parentFolderId, siblings);
            }
            finally { _entryMutationGate.Release(); }
            if (synchronizeSerials)
            {
                await SynchronizeFolderSerialAsync(parentFolderId, entry.Path, siblings, cancellationToken);
            }
        }

        if (entry.Type != "folder" || string.IsNullOrWhiteSpace(entry.FolderId))
        {
            if (synchronizeSerials)
            {
                await RebuildFavoritesSerialAsync(cancellationToken);
            }
            return;
        }

        if (folderMapping != null)
        {
            await _serials.DeleteFolderAsync(folderMapping, cancellationToken);
            if (!string.IsNullOrWhiteSpace(folderMapping.NamesMessageId))
            {
                await _discord.DeleteMessageAsync(_settings.NamesChannelId, folderMapping.NamesMessageId, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(folderMapping.ThreadId))
            {
                await _discord.DeleteChannelAsync(folderMapping.ThreadId, cancellationToken);
            }

            _mappings.TryRemove(entry.FolderId, out _);
            _descriptors.TryRemove(entry.FolderId, out _);
            _store.DeleteFolderIndexMapping(entry.FolderId);
            _store.CacheFolderEntries(entry.FolderId, []);
        }

        if (synchronizeSerials)
        {
            await RebuildFavoritesSerialAsync(cancellationToken);
        }
    }

    private Task RebuildFavoritesSerialAsync(CancellationToken cancellationToken)
    {
        return RebuildFavoritesSerialAsync([], cancellationToken);
    }

    private async Task RebuildFavoritesSerialAsync(
        IEnumerable<AppEntry> additionalEntries,
        CancellationToken cancellationToken)
    {
        if (DeferFavoritesSerial(additionalEntries)) return;
        var favorites = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var mappings = MappingSnapshot();
        var trashedFolders = await LoadTrashFolderIdsAsync(cancellationToken);
        foreach (var mapping in mappings.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mapping.FolderId.Equals(SerialIndexService.FavoritesSerialId, StringComparison.OrdinalIgnoreCase)) continue;
            if (trashedFolders.Contains(mapping.FolderId)) continue;
            var serial = await _serials.LoadFolderAsync(mapping, cancellationToken);
            if (serial == null)
            {
                continue;
            }

            var descriptor = ResolveFolderDescriptor(mapping.FolderId, "/");
            var entries = _serials.MaterializeEntries(
                serial,
                _store.LoadCachedEntriesForFolder(mapping.FolderId),
                descriptor.Path,
                mappings);
            foreach (var favorite in entries.Where(entry => entry.Favorite))
            {
                favorites[EntryIdentity(favorite)] = favorite;
            }
        }

        foreach (var favorite in additionalEntries.Where(entry => entry.Favorite &&
                     !trashedFolders.Contains(NormalizeFolderId(entry.ParentFolderId))))
        {
            favorites[EntryIdentity(favorite)] = favorite;
        }

        var built = _serials.BuildFolderSerial(
            SerialIndexService.FavoritesSerialId,
            RootFolderId,
            SerialIndexService.FavoritesSerialId,
            favorites.Values,
            mappings);
        var result = new FolderSerial
        {
            FolderId = built.FolderId,
            ParentFolderId = built.ParentFolderId,
            FolderName = built.FolderName,
            GeneratedAt = built.GeneratedAt,
            Aggregate = built.Aggregate,
            Children = built.Children,
            Kind = FolderSerialKind.Favorites
        };
        await PublishFavoritesSerialAsync(result, cancellationToken);
    }

    private async Task PublishFavoritesSerialAsync(FolderSerial serial, CancellationToken cancellationToken)
    {
        _mappings.TryGetValue(SerialIndexService.FavoritesSerialId, out var mapping);
        var published = await _serials.SaveStandaloneSerialAsync(
            serial, cancellationToken, mapping?.SerialMessageId ?? "");
        if (published == null || mapping == null ||
            (mapping.SerialMessageId == published.Value.MessageId &&
             mapping.SerialSha256.Equals(published.Value.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var updated = mapping with
        {
            SerialMessageId = published.Value.MessageId,
            SerialSha256 = published.Value.Sha256
        };
        var message = await _discord.UpdateMessageAsync(
            _settings.NamesChannelId, mapping.NamesMessageId,
            _metadata.SerializeFolderIndex(updated), cancellationToken);
        updated = updated with { NamesMessageId = message.Id };
        _mappings[updated.FolderId] = updated;
        _store.UpsertFolderIndexMapping(updated);
    }

    private async Task<List<AppEntry>> RefreshFolderCoreAsync(
        string folderId,
        string folderPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_mappings.ContainsKey(folderId))
            {
                var directSerial = await _serials.LoadStandaloneSerialAsync(
                    folderId,
                    FolderSerialKind.Folder,
                    cancellationToken);
                if (directSerial != null)
                {
                    var directEntries = _serials.MaterializeEntries(
                            directSerial,
                            _store.LoadCachedEntriesForFolder(folderId),
                            folderPath,
                            MappingSnapshot())
                        .ToList();
                    NormalizeFolderEntries(directEntries, folderId, folderPath);
                    RecordDescriptors(directEntries);
                    _store.CacheFolderEntries(folderId, directEntries);
                    return directEntries;
                }
            }

            var mapping = await GetMappingAsync(folderId, cancellationToken);
            var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
            var messages = await _discord.GetMessagesInParallelAsync(mapping.ThreadId, workers, cancellationToken);
            var envelopes = await _metadata.ReadEnvelopesInParallelAsync(messages, workers, cancellationToken);
            if (messages.Count > 0 && envelopes.Count == 0 && _store.LoadCachedEntriesForFolder(folderId).Count > 0)
            {
                throw new InvalidOperationException("A leitura do índice da pasta não retornou metadados válidos; o cache foi preservado.");
            }
            var entries = _metadata.ToFolderEntries(envelopes, folderId, folderPath, MappingSnapshot());
            NormalizeFolderEntries(entries, folderId, folderPath);
            RecordDescriptors(entries);
            if (!folderId.Equals(TrashFolderId, StringComparison.OrdinalIgnoreCase))
                await ConsolidateDuplicateFolderEntriesAsync(folderId, entries, cancellationToken);
            _store.CacheFolderEntries(folderId, entries);
            await SynchronizeFolderSerialAsync(folderId, folderPath, entries, cancellationToken);
            await ArchiveFolderThreadAsync(mapping.ThreadId, cancellationToken);
            return entries;
        }
        finally
        {
            _folderRefreshes.TryRemove(folderId, out _);
        }
    }

    private async Task ConsolidateDuplicateFolderEntriesAsync(
        string parentFolderId,
        List<AppEntry> parentEntries,
        CancellationToken cancellationToken)
    {
        var groups = parentEntries
            .Where(entry => entry.Type == "folder" && !string.IsNullOrWhiteSpace(entry.FolderId) &&
                            entry.Sync == null && entry.SyncDevice == null)
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();
        if (groups.Count == 0 || !_duplicateFolderRepairs.TryAdd(parentFolderId, 0))
        {
            return;
        }

        try
        {
            foreach (var group in groups)
            {
                var canonical = group
                    .OrderBy(entry =>
                    {
                        var messageId = Snowflake(entry.MetadataMessageId);
                        return messageId == 0 ? ulong.MaxValue : messageId;
                    })
                    .ThenBy(entry => entry.FolderId, StringComparer.Ordinal)
                    .First();
                if (!_mappings.ContainsKey(canonical.FolderId))
                {
                    AppLogger.Error(
                        "Registry.DuplicateFolder",
                        new InvalidOperationException("A pasta canônica não possui mapping em names."),
                        canonical.FolderId);
                    continue;
                }

                var canonicalPath = PathRules.ChildPath(canonical);
                var canonicalMapping = await GetMappingAsync(canonical.FolderId, cancellationToken);
                var serialFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    canonical.FolderId
                };
                await RefreshFolderAsync(canonical.FolderId, canonicalPath, cancellationToken);
                foreach (var duplicate in group.Where(entry => !entry.FolderId.Equals(canonical.FolderId, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!_mappings.ContainsKey(duplicate.FolderId))
                    {
                        AppLogger.Error(
                            "Registry.DuplicateFolder",
                            new InvalidOperationException("A pasta duplicada não possui mapping em names."),
                            duplicate.FolderId);
                        continue;
                    }

                    try
                    {
                        var duplicateMapping = await GetMappingAsync(duplicate.FolderId, cancellationToken);
                        var children = await RefreshFolderAsync(
                            duplicate.FolderId,
                            PathRules.ChildPath(duplicate),
                            cancellationToken);
                        await _discord.UnarchiveThreadAsync(canonicalMapping.ThreadId, cancellationToken);
                        await _discord.UnarchiveThreadAsync(duplicateMapping.ThreadId, cancellationToken);
                        try
                        {
                            foreach (var child in children)
                            {
                                if (child.Type == "folder" && !string.IsNullOrWhiteSpace(child.FolderId))
                                {
                                    serialFolderIds.Add(child.FolderId);
                                }

                                await CopyDuplicateChildAsync(
                                    child,
                                    duplicate.FolderId,
                                    duplicateMapping,
                                    canonical.FolderId,
                                    canonicalMapping,
                                    canonicalPath,
                                    cancellationToken);
                            }
                        }
                        finally
                        {
                            await ArchiveFolderThreadAsync(duplicateMapping.ThreadId, cancellationToken);
                            await ArchiveFolderThreadAsync(canonicalMapping.ThreadId, cancellationToken);
                        }

                        await DeleteEntryAsync(duplicate, cancellationToken, synchronizeSerials: false);
                        parentEntries.Remove(duplicate);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Keep the duplicate visible until all of its children can be preserved.
                        AppLogger.Error("Registry.DuplicateFolder", ex, duplicate.FolderId);
                    }
                }

                await SynchronizeFolderSerialsAsync(serialFolderIds, cancellationToken);
            }
        }
        finally
        {
            _duplicateFolderRepairs.TryRemove(parentFolderId, out _);
        }
    }

    private async Task CopyDuplicateChildAsync(
        AppEntry child,
        string sourceFolderId,
        FolderIndexMapping sourceMapping,
        string destinationFolderId,
        FolderIndexMapping destinationMapping,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceMessageId = child.MetadataMessageId;
        var destinationChildren = _store.LoadCachedEntriesForFolder(destinationFolderId);
        var alreadyPresent = destinationChildren.Any(existing => SameEntryIdentity(existing, child));
        if (!alreadyPresent)
        {
            var sourceParentFolderId = child.ParentFolderId;
            var sourcePath = child.Path;
            child.ParentFolderId = destinationFolderId;
            child.Path = destinationPath;
            child.MetadataMessageId = "";
            try
            {
                await _metadata.SendEntryAsync(_discord, destinationMapping.ThreadId, child, cancellationToken);
            }
            catch
            {
                child.ParentFolderId = sourceParentFolderId;
                child.Path = sourcePath;
                child.MetadataMessageId = sourceMessageId;
                throw;
            }

            ReplaceEntry(destinationChildren, child);
            _store.CacheFolderEntries(destinationFolderId, destinationChildren);
            RecordDescriptors(destinationChildren);
        }

        if (string.IsNullOrWhiteSpace(sourceMessageId))
        {
            throw new InvalidOperationException("O filho da pasta duplicada não possui a mensagem de metadata.");
        }

        await _discord.DeleteMessageAsync(sourceMapping.ThreadId, sourceMessageId, cancellationToken);
        var sourceChildren = _store.LoadCachedEntriesForFolder(sourceFolderId);
        RemoveEntry(sourceChildren, child);
        _store.CacheFolderEntries(sourceFolderId, sourceChildren);
    }

    private async Task<FolderIndexMapping> GetMappingAsync(string folderId, CancellationToken cancellationToken)
    {
        var normalizedFolderId = NormalizeFolderId(folderId);
        if (_mappings.TryGetValue(normalizedFolderId, out var cached))
        {
            return cached;
        }

        await RefreshMappingsAsync(cancellationToken);
        if (_mappings.TryGetValue(normalizedFolderId, out var refreshed))
        {
            return refreshed;
        }

        if (normalizedFolderId.Equals(RootFolderId, StringComparison.OrdinalIgnoreCase))
        {
            return await EnsureRootIndexAsync(cancellationToken);
        }

        throw new InvalidOperationException($"O índice da pasta {normalizedFolderId} não está registrado no canal names.");
    }

    private async Task<FolderIndexMapping> GetFolderMappingForDeletionAsync(
        string folderId,
        AppEntry? fallbackFolder,
        CancellationToken cancellationToken)
    {
        var normalizedFolderId = NormalizeFolderId(folderId);
        try
        {
            return await GetMappingAsync(normalizedFolderId, cancellationToken);
        }
        catch (InvalidOperationException) when (
            TryGetFolderIndexThreadId(
                fallbackFolder ?? _store.LoadCachedFolderEntry(normalizedFolderId),
                out var threadId))
        {
            return new FolderIndexMapping(normalizedFolderId, threadId, "");
        }
    }

    private static bool TryGetFolderIndexThreadId(AppEntry? folder, out string threadId)
    {
        threadId = folder?.ThreadId ?? "";
        return folder?.Type == "folder" &&
               !string.IsNullOrWhiteSpace(threadId) &&
               !threadId.StartsWith("folder_", StringComparison.Ordinal);
    }

    private async Task<FolderIndexMapping> EnsureRootIndexAsync(CancellationToken cancellationToken)
    {
        await _rootCreationGate.WaitAsync(cancellationToken);
        try
        {
            if (_mappings.TryGetValue(RootFolderId, out var existing))
            {
                return existing;
            }

            if (_mappings.Count > 0)
            {
                throw new InvalidOperationException("O canal names possui índices de pasta, mas não possui o índice root.");
            }

            var root = await CreateFolderIndexAsync(RootFolderId, RootFolderId, RootFolderId, "/", cancellationToken);
            return root;
        }
        finally
        {
            _rootCreationGate.Release();
        }
    }

    private async Task<FolderIndexMapping> EnsureFolderIndexAsync(
        AppEntry folder,
        CancellationToken cancellationToken,
        bool synchronizeSerials = true)
    {
        if (_mappings.TryGetValue(folder.FolderId, out var existing))
        {
            return existing;
        }

        return await CreateFolderIndexAsync(
            folder.FolderId,
            NormalizeFolderId(folder.ParentFolderId),
            folder.Name,
            PathRules.ChildPath(folder),
            cancellationToken,
            synchronizeSerials);
    }

    private async Task<FolderIndexMapping> CreateFolderIndexAsync(
        string folderId,
        string parentFolderId,
        string name,
        string folderPath,
        CancellationToken cancellationToken,
        bool synchronizeSerials = true)
    {
        var header = _metadata.SerializeFolderIndexHeader(folderId, parentFolderId, name);
        var post = await _discord.CreateForumPostAsync(
            _settings.FoldersForumId,
            FolderThreadTitle(name, folderId),
            header,
            cancellationToken);
        var draft = new FolderIndexMapping(folderId, post.ThreadId, "");
        var message = await _discord.CreateMessageAsync(
            _settings.NamesChannelId,
            _metadata.SerializeFolderIndex(draft),
            cancellationToken);
        var mapping = draft with { NamesMessageId = message.Id };
        _mappings[folderId] = mapping;
        _store.UpsertFolderIndexMapping(mapping);
        _descriptors[folderId] = new FolderDescriptor(folderId, parentFolderId, name, folderPath);
        if (synchronizeSerials)
        {
            await SynchronizeFolderSerialAsync(folderId, folderPath, [], cancellationToken);
        }
        else
        {
            _deferredFolderSerials[folderId] = 0;
        }
        await ArchiveFolderThreadAsync(post.ThreadId, cancellationToken);
        return _mappings[folderId];
    }

    private async Task SynchronizeFolderSerialAsync(
        string folderId,
        string folderPath,
        IEnumerable<AppEntry> entries,
        CancellationToken cancellationToken)
    {
        if (DeferFolderSerial(folderId)) return;
        await _serialSynchronizationGate.WaitAsync(cancellationToken);
        try
        {
            _folderFileCounts.Clear();
            _libraryStatistics.Clear();
            var normalizedFolderId = NormalizeFolderId(folderId);
            if (!_mappings.TryGetValue(normalizedFolderId, out var mapping))
            {
                return;
            }

            var descriptor = ResolveFolderDescriptor(normalizedFolderId, folderPath);
            var serial = _serials.BuildFolderSerial(
                normalizedFolderId,
                descriptor.ParentFolderId,
                descriptor.Name,
                entries,
                MappingSnapshot());
            var updated = await _serials.SaveFolderAsync(serial, mapping, cancellationToken);
            if (updated == mapping)
            {
                return;
            }

            DiscordMessageDto message;
            try
            {
                message = await _discord.UpdateMessageAsync(
                    _settings.NamesChannelId,
                    mapping.NamesMessageId,
                    _metadata.SerializeFolderIndex(updated),
                    cancellationToken);
            }
            catch
            {
                await _serials.TryDeleteRemoteSerialAsync(
                    updated.SerialMessageId,
                    mapping.SerialMessageId,
                    CancellationToken.None);
                await _serials.InvalidateCachedFolderAsync(normalizedFolderId);
                throw;
            }

            var persisted = updated with { NamesMessageId = message.Id };
            _mappings[normalizedFolderId] = persisted;
            _store.UpsertFolderIndexMapping(persisted);
            await _serials.DeleteSupersededRemoteSerialAsync(
                mapping.SerialMessageId,
                persisted.SerialMessageId,
                cancellationToken);
        }
        finally
        {
            _serialSynchronizationGate.Release();
        }
    }

    private async Task<long?> CountFolderFilesAsync(
        string folderId,
        HashSet<string> visitedFolderIds,
        CancellationToken cancellationToken)
    {
        if (!visitedFolderIds.Add(folderId))
        {
            return null;
        }

        var mapping = await GetMappingAsync(folderId, cancellationToken);
        var serial = await _serials.LoadFolderAsync(mapping, cancellationToken);
        if (serial != null)
        {
            long count = serial.Children.LongCount(child => !child.IsFolder);
            foreach (var child in serial.Children.Where(child => child.IsFolder))
            {
                if (string.IsNullOrWhiteSpace(child.Id))
                {
                    return null;
                }

                FolderIndexMapping childMapping;
                try
                {
                    childMapping = await GetMappingAsync(child.Id, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }

                var nested = await CountFolderFilesAsync(childMapping.FolderId, visitedFolderIds, cancellationToken);
                if (!nested.HasValue)
                {
                    return null;
                }

                count += nested.Value;
            }

            return count;
        }

        var descriptor = ResolveFolderDescriptor(folderId, "/");
        var refreshedEntries = await RefreshFolderAsync(folderId, descriptor.Path, cancellationToken);
        long refreshedCount = refreshedEntries.LongCount(entry => entry.Type != "folder");
        foreach (var child in refreshedEntries.Where(entry => entry.Type == "folder"))
        {
            if (string.IsNullOrWhiteSpace(child.FolderId))
            {
                return null;
            }

            var nested = await CountFolderFilesAsync(child.FolderId, visitedFolderIds, cancellationToken);
            if (!nested.HasValue)
            {
                return null;
            }

            refreshedCount += nested.Value;
        }

        return refreshedCount;
    }

    private async Task<LibraryStatistics?> ReadLibraryTotalsAsync(string folderId, CancellationToken token, bool cachedOnly = false)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = await ReadLibraryStatisticsAsync(folderId, visited, token, cachedOnly);
        if (total == null || folderId != RootFolderId || !_mappings.ContainsKey(TrashFolderId) || visited.Contains(TrashFolderId)) return total;
        var trash = await ReadLibraryStatisticsAsync(TrashFolderId, visited, token, cachedOnly);
        return trash == null ? null : new LibraryStatistics(total.FileCount + trash.FileCount,
            total.FolderCount + trash.FolderCount, total.TotalBytes + trash.TotalBytes);
    }

    private async Task<LibraryStatistics?> ReadLibraryStatisticsAsync(
        string folderId,
        HashSet<string> visitedFolderIds,
        CancellationToken cancellationToken,
        bool cachedOnly = false)
    {
        if (!visitedFolderIds.Add(folderId))
        {
            return null;
        }

        var serial = cachedOnly
            ? await _serials.LoadCachedFolderAsync(folderId, cancellationToken)
            : await _serials.LoadFolderAsync(await GetMappingAsync(folderId, cancellationToken), cancellationToken);
        if (serial != null)
        {
            long fileCount = serial.Children.LongCount(child => !child.IsFolder);
            long folderCount = serial.Children.LongCount(child => child.IsFolder);
            long totalBytes = Math.Max(0, serial.Aggregate.TotalBytes);
            foreach (var child in serial.Children.Where(child => child.IsFolder))
            {
                if (string.IsNullOrWhiteSpace(child.Id))
                {
                    return null;
                }

                var childFolderId = child.Id;
                if (!cachedOnly)
                {
                    try { childFolderId = (await GetMappingAsync(child.Id, cancellationToken)).FolderId; }
                    catch (InvalidOperationException) { return null; }
                }
                var nested = await ReadLibraryStatisticsAsync(
                    childFolderId,
                    visitedFolderIds,
                    cancellationToken, cachedOnly);
                if (nested == null)
                {
                    return null;
                }

                fileCount += nested.FileCount;
                folderCount += nested.FolderCount;
                totalBytes += nested.TotalBytes;
            }

            return new LibraryStatistics(fileCount, folderCount, totalBytes);
        }

        var cachedEntries = cachedOnly
            ? _store.LoadCachedEntriesForFolder(folderId)
            : await RefreshFolderAsync(folderId, ResolveKnownFolderPath(folderId), cancellationToken);
        if (cachedOnly && cachedEntries.Count == 0) return null;
        long cachedFileCount = cachedEntries.LongCount(entry => entry.Type != "folder");
        long cachedFolderCount = cachedEntries.LongCount(entry => entry.Type == "folder");
        long cachedTotalBytes = cachedEntries
            .Where(entry => entry.Type != "folder")
            .Sum(FileVersioning.StorageBytes);
        foreach (var child in cachedEntries.Where(entry => entry.Type == "folder"))
        {
            if (string.IsNullOrWhiteSpace(child.FolderId))
            {
                return null;
            }

            var nested = await ReadLibraryStatisticsAsync(
                child.FolderId,
                visitedFolderIds,
                cancellationToken, cachedOnly);
            if (nested == null)
            {
                return null;
            }

            cachedFileCount += nested.FileCount;
            cachedFolderCount += nested.FolderCount;
            cachedTotalBytes += nested.TotalBytes;
        }

        return new LibraryStatistics(cachedFileCount, cachedFolderCount, cachedTotalBytes);
    }

    private async Task<bool> ArchiveFolderThreadAsync(
        string threadId,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || _uploadIndexThreads.ContainsKey(threadId))
        {
            return false;
        }

        try
        {
            await _discord.ArchiveThreadAsync(threadId, cancellationToken, onRateLimitWait);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (DiscordApiException ex) when ((int)ex.StatusCode is 403 or 404)
        {
            AppLogger.Error("Registry.ArchiveFolderThread", ex, threadId);
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Registry.ArchiveFolderThread", ex, threadId);
            return false;
        }
    }

    private async Task<bool> DeleteSupersededSerialIfCurrentAsync(
        string folderId,
        string expectedCurrentMessageId,
        string staleMessageId,
        CancellationToken cancellationToken)
    {
        await _serialSynchronizationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_mappings.TryGetValue(folderId, out var current) ||
                !current.SerialMessageId.Equals(expectedCurrentMessageId, StringComparison.Ordinal))
            {
                return false;
            }

            await _serials.TryDeleteRemoteSerialAsync(
                staleMessageId,
                expectedCurrentMessageId,
                cancellationToken);
            return true;
        }
        finally
        {
            _serialSynchronizationGate.Release();
        }
    }

    private FolderDescriptor ResolveFolderDescriptor(string folderId, string folderPath)
    {
        if (_descriptors.TryGetValue(folderId, out var known))
        {
            return known;
        }

        var cached = _store.LoadCachedFolderEntry(folderId);
        if (cached != null)
        {
            var descriptor = new FolderDescriptor(
                folderId,
                NormalizeFolderId(cached.ParentFolderId),
                cached.Name,
                PathRules.ChildPath(cached));
            _descriptors[folderId] = descriptor;
            return descriptor;
        }

        var name = folderId.Equals(RootFolderId, StringComparison.OrdinalIgnoreCase)
            ? RootFolderId
            : Path.GetFileName(PathRules.Normalize(folderPath));
        var fallback = new FolderDescriptor(folderId, RootFolderId, name, PathRules.Normalize(folderPath));
        _descriptors[folderId] = fallback;
        return fallback;
    }

    private string ResolveKnownFolderPath(string? folderId)
    {
        var normalized = NormalizeFolderId(folderId);
        if (normalized.Equals(RootFolderId, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }
        if (normalized.Equals(SynchronizedRootFolderId, StringComparison.OrdinalIgnoreCase))
        {
            return SynchronizedRootPath;
        }
        if (_descriptors.TryGetValue(normalized, out var descriptor))
        {
            return descriptor.Path;
        }

        var cached = _store.LoadCachedFolderEntry(normalized);
        return cached == null ? "/" : PathRules.ChildPath(cached);
    }

    private IReadOnlyDictionary<string, FolderIndexMapping> MappingSnapshot()
    {
        return new Dictionary<string, FolderIndexMapping>(_mappings, StringComparer.OrdinalIgnoreCase);
    }

    private void RecordDescriptors(IEnumerable<AppEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry.Type == "folder" && !string.IsNullOrWhiteSpace(entry.FolderId)))
        {
            _descriptors[entry.FolderId] = new FolderDescriptor(
                entry.FolderId,
                NormalizeFolderId(entry.ParentFolderId),
                entry.Name,
                PathRules.ChildPath(entry));
        }
    }

    private static void NormalizeFolderEntries(List<AppEntry> entries, string folderId, string folderPath)
    {
        foreach (var entry in entries)
        {
            entry.ParentFolderId = NormalizeFolderId(entry.ParentFolderId);
            entry.Path = PathRules.Normalize(folderPath);
            entry.Normalize();
        }
    }

    private static void ReplaceEntry(List<AppEntry> entries, AppEntry replacement)
    {
        RemoveEntry(entries, replacement);
        entries.Add(replacement);
    }

    private static bool SameEntryIdentity(AppEntry left, AppEntry right)
    {
        return (!string.IsNullOrWhiteSpace(left.FileId) &&
                left.FileId.Equals(right.FileId, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(left.FolderId) &&
                left.FolderId.Equals(right.FolderId, StringComparison.OrdinalIgnoreCase));
    }

    private static string EntryIdentity(AppEntry entry)
    {
        return entry.Type == "folder" ? "folder:" + entry.FolderId : "file:" + entry.FileId;
    }

    private static void CopyEntryState(AppEntry source, AppEntry target)
    {
        target.Name = source.Name;
        target.ThreadId = source.ThreadId;
        target.UploadedAt = source.UploadedAt;
        target.SizeBytes = source.SizeBytes;
        target.MetadataMessageId = source.MetadataMessageId;
        target.FileId = source.FileId;
        target.Storage = source.Storage;
        target.ThumbnailRef = source.ThumbnailRef;
        target.FolderId = source.FolderId;
        target.ParentFolderId = source.ParentFolderId;
        target.FolderColor = source.FolderColor;
        target.DeviceType = source.DeviceType;
        target.Favorite = source.Favorite;
        target.Type = source.Type;
        target.MimeType = source.MimeType;
        target.ChunkMessageIds = source.ChunkMessageIds.ToList();
        target.ChunkReferences = source.ChunkReferences?.ToList();
        target.Checksums = source.Checksums;
        target.ChunkCount = source.ChunkCount;
        target.BatchId = source.BatchId;
        target.VideoPreviewChunkMessageIds = source.VideoPreviewChunkMessageIds.ToList();
        target.VideoPreviewChunkReferences = source.VideoPreviewChunkReferences?.ToList();
        target.VideoPreviewMessageId = source.VideoPreviewMessageId;
        target.VideoPreviewSizeBytes = source.VideoPreviewSizeBytes;
        target.PreviewMessageId = source.PreviewMessageId;
        target.Encryption = source.Encryption?.Clone();
        target.ModifiedAt = source.ModifiedAt;
        target.VersionId = source.VersionId;
        target.VersionUploadedAt = source.VersionUploadedAt;
        target.Versions = source.Versions;
    }

    private static void CopyFilePayload(AppEntry source, AppEntry target)
    {
        target.Checksums = source.Checksums;
        target.Storage = source.Storage;
        target.ThumbnailRef = source.ThumbnailRef;
        if (source.LogicalChunkCount == 0)
            throw new InvalidDataException("Os metadados completos do arquivo não puderam ser carregados.");
        target.ChunkMessageIds = source.ChunkMessageIds.ToList();
        target.ChunkReferences = source.ChunkReferences?.ToList();
        target.ChunkCount = source.ChunkCount;
        target.BatchId = source.BatchId;
        target.PreviewMessageId = source.PreviewMessageId;
        target.VideoPreviewMessageId = source.VideoPreviewMessageId;
        target.VideoPreviewChunkMessageIds = source.VideoPreviewChunkMessageIds.ToList();
        target.VideoPreviewChunkReferences = source.VideoPreviewChunkReferences?.ToList();
        target.VideoPreviewSizeBytes = source.VideoPreviewSizeBytes;
        target.Encryption = source.Encryption?.Clone();
        target.Versions = source.Versions;
        target.VersionId = source.VersionId;
        target.VersionUploadedAt = source.VersionUploadedAt;
        target.UploadedAt = source.UploadedAt;
        if (string.IsNullOrEmpty(target.ModifiedAt)) target.ModifiedAt = source.ModifiedAt;
        target.SizeBytes = source.SizeBytes;
        if (string.IsNullOrEmpty(target.MimeType)) target.MimeType = source.MimeType;
    }

    private static FolderSerial CopySerial(FolderSerial source, IReadOnlyList<FolderSerialEntry> children)
    {
        return new FolderSerial
        {
            FolderId = source.FolderId,
            ParentFolderId = source.ParentFolderId,
            FolderName = source.FolderName,
            GeneratedAt = DateTimeOffset.UtcNow,
            Aggregate = source.Aggregate,
            Children = children,
            Kind = source.Kind
        };
    }

    private static FolderSerialEntry CopySerialEntry(FolderSerialEntry source, AppEntry entry)
    {
        return new FolderSerialEntry
        {
            Id = source.Id,
            NamesMessageId = entry.MetadataMessageId,
            ParentFolderId = NormalizeFolderId(entry.ParentFolderId),
            Name = entry.Name,
            ThreadId = entry.ThreadId,
            IsFolder = source.IsFolder,
            MimeType = source.MimeType,
            UploadedAt = source.UploadedAt,
            SizeBytes = source.SizeBytes,
            PreviewMessageId = source.PreviewMessageId,
            Encryption = source.Encryption?.Clone(),
            FolderColor = source.FolderColor,
            DeviceType = source.DeviceType,
            Favorite = entry.Favorite
        };
    }

    private static bool IsSameFolderIdentity(AppEntry candidate, AppEntry folder)
    {
        return candidate.Type == "folder" &&
               ((!string.IsNullOrWhiteSpace(folder.FolderId) &&
                 candidate.FolderId.Equals(folder.FolderId, StringComparison.OrdinalIgnoreCase)) ||
                candidate.Name.Equals(folder.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveEntry(List<AppEntry> entries, AppEntry target)
    {
        entries.RemoveAll(entry =>
            (!string.IsNullOrWhiteSpace(target.MetadataMessageId) && entry.MetadataMessageId == target.MetadataMessageId) ||
            (!string.IsNullOrWhiteSpace(target.FileId) && entry.FileId == target.FileId) ||
            (!string.IsNullOrWhiteSpace(target.FolderId) && entry.FolderId == target.FolderId));
    }

    private static string NormalizeFolderId(string? folderId)
    {
        return string.IsNullOrWhiteSpace(folderId) ? RootFolderId : folderId;
    }

    private static ulong Snowflake(string id)
    {
        return ulong.TryParse(id, out var parsed) ? parsed : 0;
    }

    private static string FolderThreadTitle(string name, string folderId)
    {
        return "index-" + Guid.NewGuid().ToString("N");
    }

    private sealed record FolderDescriptor(string FolderId, string ParentFolderId, string Name, string Path);
}
