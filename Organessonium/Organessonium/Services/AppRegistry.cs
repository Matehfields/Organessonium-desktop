using System.Collections.Concurrent;
using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class AppRegistry
{
    public const string RootFolderId = "root";

    private readonly DiscordApiClient _discord;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly MetadataService _metadata = new();
    private readonly SerialIndexService _serials;
    private readonly ConcurrentDictionary<string, FolderIndexMapping> _mappings;
    private readonly ConcurrentDictionary<string, FolderDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<List<AppEntry>>> _folderRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _folderFileCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _mappingRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _rootCreationGate = new(1, 1);
    private readonly SemaphoreSlim _entryMutationGate = new(1, 1);
    private readonly SemaphoreSlim _serialSynchronizationGate = new(1, 1);
    private int _remoteSerialCleanupStarted;

    public AppRegistry(DiscordApiClient discord, SettingsStore store, AppSettings settings)
    {
        _discord = discord;
        _store = store;
        _settings = settings;
        _serials = new SerialIndexService(discord, store, settings);
        _mappings = new ConcurrentDictionary<string, FolderIndexMapping>(
            store.LoadFolderIndexMappings(),
            StringComparer.OrdinalIgnoreCase);
        _descriptors[RootFolderId] = new FolderDescriptor(RootFolderId, RootFolderId, RootFolderId, "/");
    }

    public async Task<List<AppEntry>> LoadCachedFolderAsync(
        string folderId,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedFolderId = NormalizeFolderId(folderId);
        var mapping = await GetMappingAsync(normalizedFolderId, cancellationToken);
        var cached = _store.LoadCachedEntriesForFolder(normalizedFolderId);
        var serial = await _serials.LoadFolderAsync(mapping, cancellationToken);
        var entries = serial == null
            ? cached
            : _serials.MaterializeEntries(serial, cached, folderPath, MappingSnapshot()).ToList();
        NormalizeFolderEntries(entries, normalizedFolderId, folderPath);
        RecordDescriptors(entries);
        return entries;
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
            var folder = entries.FirstOrDefault(entry =>
                entry.Type == "folder" &&
                entry.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (folder == null || string.IsNullOrWhiteSpace(folder.FolderId))
            {
                entries = await RefreshFolderAsync(currentFolderId, currentPath, cancellationToken);
                folder = entries.FirstOrDefault(entry =>
                    entry.Type == "folder" &&
                    entry.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
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

            _mappings.Clear();
            foreach (var mapping in canonical)
            {
                _mappings[mapping.FolderId] = mapping;
            }
            _store.ReplaceFolderIndexMappings(canonical);
            _folderFileCounts.Clear();
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
        CancellationToken cancellationToken = default)
    {
        if (folder.Type != "folder" || string.IsNullOrWhiteSpace(folder.FolderId))
        {
            return [];
        }

        var result = new List<AppEntry>();
        async Task VisitAsync(string folderId, string path)
        {
            var children = await RefreshFolderAsync(folderId, path, cancellationToken);
            foreach (var child in children)
            {
                result.Add(child);
            }

            foreach (var child in children.Where(child => child.Type == "folder" && !string.IsNullOrWhiteSpace(child.FolderId)))
            {
                await VisitAsync(child.FolderId, PathRules.ChildPath(child));
            }
        }

        await VisitAsync(folder.FolderId, PathRules.ChildPath(folder));
        return result;
    }

    public async Task<string> AddEntryAsync(
        AppEntry entry,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        await _entryMutationGate.WaitAsync(cancellationToken);
        try
        {
            return await AddEntryCoreAsync(entry, cancellationToken, onRateLimitWait);
        }
        finally
        {
            _entryMutationGate.Release();
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
        Func<TimeSpan, Task>? onRateLimitWait)
    {
        entry.Normalize();
        var parentFolderId = NormalizeFolderId(entry.ParentFolderId);
        entry.ParentFolderId = parentFolderId;
        if (entry.Type == "folder")
        {
            var created = await EnsureFolderIndexAsync(entry, cancellationToken);
            entry.ThreadId = created.ThreadId;
        }

        var parent = await GetMappingAsync(parentFolderId, cancellationToken);
        await _discord.UnarchiveThreadAsync(parent.ThreadId, cancellationToken, onRateLimitWait);
        try
        {
            await _metadata.SendEntryAsync(_discord, parent.ThreadId, entry, cancellationToken, onRateLimitWait);
            var siblings = _store.LoadCachedEntriesForFolder(parentFolderId);
            ReplaceEntry(siblings, entry);
            _store.CacheFolderEntries(parentFolderId, siblings);
            RecordDescriptors(siblings);
            await SynchronizeFolderSerialAsync(parentFolderId, entry.Path, siblings, cancellationToken);

            if (entry.Type == "folder")
            {
                await SynchronizeFolderSerialAsync(entry.FolderId, PathRules.ChildPath(entry), [], cancellationToken);
            }
        }
        finally
        {
            await ArchiveFolderThreadAsync(parent.ThreadId, cancellationToken);
        }

        return entry.MetadataMessageId;
    }

    public async Task UpdateEntryAsync(
        AppEntry entry,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        entry.Normalize();
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
    }

    public async Task MoveEntryAsync(
        AppEntry entry,
        string targetFolderId,
        string targetPath,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        entry.Normalize();
        var sourceFolderId = NormalizeFolderId(entry.ParentFolderId);
        var destinationFolderId = NormalizeFolderId(targetFolderId);
        if (sourceFolderId.Equals(destinationFolderId, StringComparison.OrdinalIgnoreCase))
        {
            entry.Path = PathRules.Normalize(targetPath);
            await UpdateEntryAsync(entry, cancellationToken, onRateLimitWait);
            return;
        }

        var source = await GetMappingAsync(sourceFolderId, cancellationToken);
        await _discord.UnarchiveThreadAsync(source.ThreadId, cancellationToken, onRateLimitWait);
        try
        {
            await _metadata.DeleteEntryAsync(_discord, source.ThreadId, entry, cancellationToken);
            var sourceChildren = _store.LoadCachedEntriesForFolder(sourceFolderId);
            RemoveEntry(sourceChildren, entry);
            _store.CacheFolderEntries(sourceFolderId, sourceChildren);
            await SynchronizeFolderSerialAsync(sourceFolderId, entry.Path, sourceChildren, cancellationToken);
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
            await SynchronizeFolderSerialAsync(destinationFolderId, entry.Path, destinationChildren, cancellationToken);
        }
        finally
        {
            await ArchiveFolderThreadAsync(destination.ThreadId, cancellationToken);
        }

        if (entry.Type == "folder")
        {
            var children = _store.LoadCachedEntriesForFolder(entry.FolderId);
            await SynchronizeFolderSerialAsync(entry.FolderId, PathRules.ChildPath(entry), children, cancellationToken);
        }
    }

    public async Task DeleteEntryAsync(AppEntry entry, CancellationToken cancellationToken = default)
    {
        var folderMapping = entry.Type == "folder" && !string.IsNullOrWhiteSpace(entry.FolderId)
            ? await GetMappingAsync(entry.FolderId, cancellationToken)
            : null;

        var parentFolderId = NormalizeFolderId(entry.ParentFolderId);
        var parent = await GetMappingAsync(parentFolderId, cancellationToken);
        await _discord.UnarchiveThreadAsync(parent.ThreadId, cancellationToken);
        try
        {
            await _metadata.DeleteEntryAsync(_discord, parent.ThreadId, entry, cancellationToken);
            var siblings = _store.LoadCachedEntriesForFolder(parentFolderId);
            RemoveEntry(siblings, entry);
            _store.CacheFolderEntries(parentFolderId, siblings);
            await SynchronizeFolderSerialAsync(parentFolderId, entry.Path, siblings, cancellationToken);
        }
        finally
        {
            await ArchiveFolderThreadAsync(parent.ThreadId, cancellationToken);
        }

        if (entry.Type != "folder" || string.IsNullOrWhiteSpace(entry.FolderId))
        {
            return;
        }

        if (folderMapping != null)
        {
            await _serials.DeleteFolderAsync(folderMapping, cancellationToken);
            await _discord.DeleteMessageAsync(_settings.NamesChannelId, folderMapping.NamesMessageId, cancellationToken);
            await _discord.DeleteChannelAsync(folderMapping.ThreadId, cancellationToken);
            _mappings.TryRemove(entry.FolderId, out _);
            _descriptors.TryRemove(entry.FolderId, out _);
            _store.DeleteFolderIndexMapping(entry.FolderId);
            _store.CacheFolderEntries(entry.FolderId, []);
        }
    }

    private async Task<List<AppEntry>> RefreshFolderCoreAsync(
        string folderId,
        string folderPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var mapping = await GetMappingAsync(folderId, cancellationToken);
            var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
            var messages = await _discord.GetMessagesInParallelAsync(mapping.ThreadId, workers, cancellationToken);
            var envelopes = await _metadata.ReadEnvelopesInParallelAsync(messages, workers, cancellationToken);
            var entries = _metadata.ToFolderEntries(envelopes, folderId, folderPath, MappingSnapshot());
            NormalizeFolderEntries(entries, folderId, folderPath);
            _store.CacheFolderEntries(folderId, entries);
            RecordDescriptors(entries);
            await SynchronizeFolderSerialAsync(folderId, folderPath, entries, cancellationToken);
            await ArchiveFolderThreadAsync(mapping.ThreadId, cancellationToken);
            return entries;
        }
        finally
        {
            _folderRefreshes.TryRemove(folderId, out _);
        }
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

    private async Task<FolderIndexMapping> EnsureFolderIndexAsync(AppEntry folder, CancellationToken cancellationToken)
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
            cancellationToken);
    }

    private async Task<FolderIndexMapping> CreateFolderIndexAsync(
        string folderId,
        string parentFolderId,
        string name,
        string folderPath,
        CancellationToken cancellationToken)
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
        await SynchronizeFolderSerialAsync(folderId, folderPath, [], cancellationToken);
        await ArchiveFolderThreadAsync(post.ThreadId, cancellationToken);
        return _mappings[folderId];
    }

    private async Task SynchronizeFolderSerialAsync(
        string folderId,
        string folderPath,
        IEnumerable<AppEntry> entries,
        CancellationToken cancellationToken)
    {
        await _serialSynchronizationGate.WaitAsync(cancellationToken);
        try
        {
            _folderFileCounts.Clear();
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
        if (serial == null)
        {
            return null;
        }

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

    private async Task<bool> ArchiveFolderThreadAsync(
        string threadId,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        if (string.IsNullOrWhiteSpace(threadId))
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
        var suffix = folderId[..Math.Min(12, folderId.Length)];
        return "index-" + suffix + "-" + (string.IsNullOrWhiteSpace(name) ? "folder" : name);
    }

    private sealed record FolderDescriptor(string FolderId, string ParentFolderId, string Name, string Path);
}
