using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

public sealed class SerialIndexService
{
    private const string RootFolderId = "root";
    public const string FavoritesSerialId = "favorites";
    private readonly DiscordApiClient _discord;
    private readonly AppSettings _settings;
    private readonly DirectoryInfo _cacheDirectory;
    private readonly FolderSerialCodec _codec = new();
    private readonly IMetadataCodec _metadataCodec;
    private readonly SerialMetadataTransport _transport;
    private readonly ConcurrentDictionary<string, FolderSerial> _loadedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _verifiedRemoteSerialIds = new(StringComparer.OrdinalIgnoreCase);

    public SerialIndexService(
        DiscordApiClient discord,
        SettingsStore store,
        AppSettings settings)
        : this(discord, settings, store.SerialCacheDirectory, MetadataCodec.ForStore(store))
    {
    }

    public SerialIndexService(
        DiscordApiClient discord,
        AppSettings settings,
        DirectoryInfo cacheDirectory,
        IMetadataCodec? metadataCodec = null)
    {
        _discord = discord;
        _settings = settings;
        _cacheDirectory = cacheDirectory;
        _metadataCodec = metadataCodec ?? MetadataCodec.Standard();
        _transport = new SerialMetadataTransport(_metadataCodec);
    }

    public Task<FolderSerial?> LoadCachedFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        return ReadCachedFolderAsync(folderId, cancellationToken);
    }

    public async Task<string?> CachedSerialSha256Async(string serialId, CancellationToken cancellationToken = default)
    {
        var file = CacheFile(serialId);
        return file.Exists
            ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file.FullName, cancellationToken))).ToLowerInvariant()
            : null;
    }

    public async Task<FolderSerial?> LoadFolderAsync(
        FolderIndexMapping mapping,
        CancellationToken cancellationToken = default)
    {
        if (_loadedFolders.TryGetValue(mapping.FolderId, out var loaded))
        {
            return loaded;
        }

        var cached = await ReadCachedFolderAsync(mapping.FolderId, cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId) ||
            string.IsNullOrWhiteSpace(mapping.SerialMessageId))
        {
            return null;
        }

        try
        {
            var message = await _discord.GetMessageAsync(
                _settings.SerialChannelId,
                mapping.SerialMessageId,
                cancellationToken);
            var attachment = message.Attachments.FirstOrDefault(candidate =>
                candidate.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
            if (attachment == null)
            {
                throw new FormatException("Serial sem anexo binário.");
            }

            var bytes = await _discord.DownloadAttachmentBytesAsync(attachment, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(mapping.SerialSha256) &&
                !hash.Equals(mapping.SerialSha256, StringComparison.OrdinalIgnoreCase))
            {
                if (!bytes.AsSpan().StartsWith("OSER"u8)) throw new MetadataCodecException(MetadataError.AuthenticationFailed);
                throw new InvalidDataException("A validação SHA-256 do serial falhou.");
            }

            var serial = _codec.DecodeFolder(_transport.Decode(bytes));
            if (!serial.FolderId.Equals(mapping.FolderId, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("O ID do serial não corresponde ao conteúdo.");
            }

            await WriteCachedAsync(mapping.FolderId, bytes, cancellationToken);
            _loadedFolders[mapping.FolderId] = serial;
            _verifiedRemoteSerialIds[mapping.FolderId] = mapping.SerialMessageId;
            return serial;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiscordApiException ex) when ((int)ex.StatusCode == 404)
        {
            _verifiedRemoteSerialIds.TryRemove(mapping.FolderId, out _);
            await InvalidateCachedFolderAsync(mapping.FolderId);
            AppLogger.Error("Serial.LoadFolder", ex, mapping.FolderId);
            return null;
        }
        catch (MetadataCodecException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Error("Serial.LoadFolder", ex, mapping.FolderId);
            return null;
        }
    }

    public async Task<FolderSerial?> LoadStandaloneSerialAsync(
        string serialId,
        FolderSerialKind expectedKind,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var normalizedId = serialId.Trim();
        if (normalizedId.Length == 0 || string.IsNullOrWhiteSpace(_settings.SerialChannelId))
        {
            return null;
        }

        var cached = await ReadCachedFolderAsync(normalizedId, cancellationToken);
        if (!forceRefresh && cached?.Kind == expectedKind)
        {
            return cached;
        }

        var messages = await _discord.GetMessagesInParallelAsync(
            _settings.SerialChannelId,
            Math.Clamp(_settings.SearchWorkers, 1, 8),
            cancellationToken);
        var candidates = messages
            .Where(message => SerialMessageEnvelope.TryParse(message.Content, out var envelope, _metadataCodec) &&
                              envelope.FolderId.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(message => ulong.TryParse(message.Id, out var id) ? id : 0)
            .ToList();
        if (candidates.Count == 0)
        {
            if (cached?.Kind == expectedKind)
            {
                return cached;
            }
            return null;
        }

        foreach (var message in candidates)
        {
            var attachment = message.Attachments.FirstOrDefault(candidate =>
                candidate.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
            if (attachment == null || !SerialMessageEnvelope.TryParse(message.Content, out var envelope, _metadataCodec))
            {
                continue;
            }

            try
            {
                var bytes = await _discord.DownloadAttachmentBytesAsync(attachment, cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!hash.Equals(envelope.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (_metadataCodec.IsEncrypted(message.Content)) throw new MetadataCodecException(MetadataError.AuthenticationFailed);
                    continue;
                }

                var serial = _codec.DecodeFolder(_transport.Decode(bytes));
                if (!serial.FolderId.Equals(normalizedId, StringComparison.OrdinalIgnoreCase) || serial.Kind != expectedKind)
                {
                    continue;
                }

                await WriteCachedAsync(normalizedId, bytes, cancellationToken);
                _loadedFolders[normalizedId] = serial;
                return serial;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MetadataCodecException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Error("Serial.LoadStandalone", ex, normalizedId);
            }
        }

        return cached?.Kind == expectedKind ? cached : null;
    }

    public async Task<(string MessageId, string Sha256)?> SaveStandaloneSerialAsync(
        FolderSerial serial,
        CancellationToken cancellationToken = default,
        string preferredMessageId = "")
    {
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId))
        {
            return null;
        }

        var rawBytes = _codec.EncodeFolder(serial, FolderSerialCodec.PublishedVersion);
        var messages = await _discord.GetMessagesInParallelAsync(
            _settings.SerialChannelId,
            Math.Clamp(_settings.SearchWorkers, 1, 8),
            cancellationToken);
        var existing = messages
            .Where(message => SerialMessageEnvelope.TryParse(message.Content, out var envelope, _metadataCodec) &&
                              envelope.FolderId.Equals(serial.FolderId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (serial.Children.Count == 0 && serial.Kind != FolderSerialKind.Favorites &&
            !serial.FolderId.Equals("synchronized", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var message in existing)
            {
                await TryDeleteRemoteSerialAsync(message.Id, cancellationToken: cancellationToken);
            }
            await InvalidateCachedFolderAsync(serial.FolderId);
            return null;
        }

        var current = existing.FirstOrDefault(message => message.Id == preferredMessageId)
            ?? existing.OrderByDescending(message => ulong.TryParse(message.Id, out var id) ? id : 0).FirstOrDefault();
        var previous = await ReadCachedFolderAsync(serial.FolderId, cancellationToken);
        var cachedHash = await CachedSerialSha256Async(serial.FolderId, cancellationToken);
        var unchanged = current != null && previous != null &&
            SerialMessageEnvelope.TryParse(current.Content, out var currentEnvelope, _metadataCodec) &&
            currentEnvelope.Sha256.Equals(cachedHash, StringComparison.OrdinalIgnoreCase) &&
            FolderSerialCodec.SameContent(previous, _codec.DecodeFolder(rawBytes));
        byte[]? bytes = null;
        var hash = cachedHash ?? "";

        string preservedMessageId;
        if (unchanged)
        {
            preservedMessageId = current!.Id;
        }
        else
        {
            bytes = _transport.Encode(rawBytes);
            hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var temporary = await WriteTemporarySerialAsync(serial.FolderId, bytes, cancellationToken);
            try
            {
                var envelope = _metadataCodec.Encode(SerialMessageEnvelope.Encode(serial.FolderId, "", hash));
                var sent = current == null
                    ? await _discord.SendFileMessageAsync(_settings.SerialChannelId, envelope, temporary, cancellationToken)
                    : await _discord.ReplaceFileMessageAsync(_settings.SerialChannelId, current.Id, envelope, temporary, cancellationToken);
                preservedMessageId = sent.Id;
            }
            finally
            {
                try { temporary.Directory?.Delete(true); } catch { }
            }
        }

        if (bytes != null) await WriteCachedAsync(serial.FolderId, bytes, cancellationToken);
        _loadedFolders[serial.FolderId] = _codec.DecodeFolder(rawBytes);
        foreach (var duplicate in existing.Where(message => message.Id != preservedMessageId))
        {
            await TryDeleteRemoteSerialAsync(duplicate.Id, preservedMessageId, cancellationToken);
        }
        return (preservedMessageId, hash);
    }

    public FolderSerial BuildFolderSerial(
        string folderId,
        string parentFolderId,
        string folderName,
        IEnumerable<AppEntry> entries,
        IReadOnlyDictionary<string, FolderIndexMapping> mappings)
    {
        var source = entries.Where(entry => entry != null).ToList();
        var normalizedFolderId = string.IsNullOrWhiteSpace(folderId) ? RootFolderId : folderId;
        var normalizedParentId = string.IsNullOrWhiteSpace(parentFolderId) ? RootFolderId : parentFolderId;
        var children = source
            .Select(entry => ToSerialEntry(entry, mappings))
            .OrderByDescending(entry => entry.IsFolder)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();

        var files = children.Where(entry => !entry.IsFolder).ToList();
        var typeCounts = files
            .Select(entry => Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant())
            .Where(extension => extension.Length > 0)
            .GroupBy(extension => extension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.OrdinalIgnoreCase);
        var aggregate = new FolderSerialAggregate
        {
            TotalItems = children.Count,
            FileCount = files.Count,
            FolderCount = children.Count - files.Count,
            TotalBytes = source.Where(entry => entry.Type != "folder").Sum(entry => Math.Max(0, entry.SizeBytes)),
            ImageCount = files.Count(IsImage),
            VideoCount = files.Count(IsVideo),
            TypeCounts = typeCounts
        };

        return new FolderSerial
        {
            FolderId = normalizedFolderId,
            ParentFolderId = normalizedFolderId.Equals(RootFolderId, StringComparison.OrdinalIgnoreCase)
                ? RootFolderId
                : normalizedParentId,
            FolderName = normalizedFolderId.Equals(RootFolderId, StringComparison.OrdinalIgnoreCase)
                ? RootFolderId
                : folderName,
            GeneratedAt = DateTimeOffset.UtcNow,
            Aggregate = aggregate,
            Children = children
        };
    }

    public async Task<FolderIndexMapping> SaveFolderAsync(
        FolderSerial serial,
        FolderIndexMapping mapping,
        CancellationToken cancellationToken = default)
    {
        var previous = await ReadCachedFolderAsync(serial.FolderId, cancellationToken);
        var remoteMissing = await RequiresRemoteUploadAsync(mapping, cancellationToken);
        var bytes = _codec.EncodeFolder(serial, FolderSerialCodec.PublishedVersion);
        var requiresRemoteUpload = remoteMissing ||
            previous == null ||
            !string.Equals(await CachedSerialSha256Async(serial.FolderId, cancellationToken), mapping.SerialSha256, StringComparison.OrdinalIgnoreCase);
        if (previous != null &&
            FolderSerialCodec.SameContent(previous, _codec.DecodeFolder(bytes)) &&
            !requiresRemoteUpload)
        {
            return mapping;
        }

        var serialForCache = _codec.DecodeFolder(bytes);
        bytes = _transport.Encode(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId))
        {
            await WriteCachedAsync(serial.FolderId, bytes, cancellationToken);
            _loadedFolders[serial.FolderId] = serialForCache;
            _verifiedRemoteSerialIds.TryRemove(serial.FolderId, out _);
            return mapping with { SerialMessageId = "", SerialSha256 = "" };
        }

        var temporary = await WriteTemporarySerialAsync(serial.FolderId, bytes, cancellationToken);
        try
        {
            var envelope = _metadataCodec.Encode(SerialMessageEnvelope.Encode(serial.FolderId, "", hash));
            var message = remoteMissing
                ? await _discord.SendFileMessageAsync(_settings.SerialChannelId, envelope, temporary, cancellationToken)
                : await _discord.ReplaceFileMessageAsync(_settings.SerialChannelId, mapping.SerialMessageId, envelope, temporary, cancellationToken);
            _verifiedRemoteSerialIds[serial.FolderId] = message.Id;
            await WriteCachedAsync(serial.FolderId, bytes, cancellationToken);
            _loadedFolders[serial.FolderId] = serialForCache;
            return mapping with { SerialMessageId = message.Id, SerialSha256 = hash };
        }
        finally
        {
            try { temporary.Directory?.Delete(true); } catch { }
        }
    }

    public async Task DeleteFolderAsync(
        FolderIndexMapping mapping,
        CancellationToken cancellationToken = default)
    {
        _loadedFolders.TryRemove(mapping.FolderId, out _);
        _verifiedRemoteSerialIds.TryRemove(mapping.FolderId, out _);
        var file = CacheFile(mapping.FolderId);
        try { if (file.Exists) file.Delete(); } catch (Exception ex) { AppLogger.Error("Serial.DeleteCache", ex, file.FullName); }
        await TryDeleteRemoteSerialAsync(mapping.SerialMessageId, cancellationToken: cancellationToken);
    }

    public Task InvalidateCachedFolderAsync(string folderId)
    {
        _loadedFolders.TryRemove(folderId, out _);
        var file = CacheFile(folderId);
        try
        {
            if (file.Exists)
            {
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Serial.InvalidateCache", ex, file.FullName);
        }

        return Task.CompletedTask;
    }

    public async Task<int> ResetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _loadedFolders.Clear();
        _verifiedRemoteSerialIds.Clear();
        _cacheDirectory.Create();
        foreach (var file in _cacheDirectory.EnumerateFiles("*.bin"))
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Serial.ResetCache", ex, file.FullName);
                throw;
            }
        }

        return 0;
    }

    private async Task<bool> RequiresRemoteUploadAsync(
        FolderIndexMapping mapping,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(mapping.SerialMessageId))
        {
            return true;
        }

        if (_verifiedRemoteSerialIds.TryGetValue(mapping.FolderId, out var verifiedMessageId) &&
            verifiedMessageId.Equals(mapping.SerialMessageId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            await _discord.GetMessageAsync(_settings.SerialChannelId, mapping.SerialMessageId, cancellationToken);
            _verifiedRemoteSerialIds[mapping.FolderId] = mapping.SerialMessageId;
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiscordApiException ex) when ((int)ex.StatusCode == 404)
        {
            _verifiedRemoteSerialIds.TryRemove(mapping.FolderId, out _);
            return true;
        }
        catch (Exception ex)
        {
            // A transient verification failure must not create a duplicate serial.
            AppLogger.Error("Serial.VerifyRemote", ex, mapping.FolderId);
            return false;
        }
    }

    public async Task DeleteSupersededRemoteSerialAsync(
        string previousMessageId,
        string currentMessageId,
        CancellationToken cancellationToken = default)
    {
        await TryDeleteRemoteSerialAsync(previousMessageId, currentMessageId, cancellationToken);
    }

    public async Task TryDeleteRemoteSerialAsync(
        string messageId,
        string? preservedMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId) ||
            string.IsNullOrWhiteSpace(messageId) ||
            messageId.Equals(preservedMessageId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _discord.DeleteMessageAsync(_settings.SerialChannelId, messageId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiscordApiException ex) when ((int)ex.StatusCode == 404)
        {
            // The message was already removed by a previous reconciliation.
        }
        catch (Exception ex)
        {
            AppLogger.Error("Serial.DeleteRemote", ex, messageId);
        }
    }

    public async Task<int> CleanupSupersededRemoteSerialsAsync(
        IReadOnlyDictionary<string, FolderIndexMapping> mappings,
        int workers,
        Func<string, string, string, CancellationToken, Task<bool>>? deleteIfCurrent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId) || mappings.Count == 0)
        {
            return 0;
        }

        var currentByFolder = mappings.Values
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.SerialMessageId))
            .GroupBy(mapping => mapping.FolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (currentByFolder.Count == 0)
        {
            return 0;
        }

        var messages = await _discord.GetMessagesInParallelAsync(
            _settings.SerialChannelId,
            Math.Clamp(workers, 1, 8),
            cancellationToken);
        var records = new List<(string MessageId, SerialMessageEnvelope Envelope)>();
        foreach (var message in messages)
        {
            if (message.Author?.Bot != true ||
                !message.Attachments.Any(attachment => attachment.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) ||
                !SerialMessageEnvelope.TryParse(message.Content, out var envelope, _metadataCodec))
            {
                continue;
            }

            records.Add((message.Id, envelope));
        }

        var removed = 0;
        foreach (var group in records.GroupBy(record => record.Envelope.FolderId, StringComparer.OrdinalIgnoreCase))
        {
            if (!currentByFolder.TryGetValue(group.Key, out var mapping) ||
                !group.Any(record => record.MessageId.Equals(mapping.SerialMessageId, StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (var record in group.Where(record => !record.MessageId.Equals(mapping.SerialMessageId, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deleteIfCurrent != null)
                {
                    if (await deleteIfCurrent(group.Key, mapping.SerialMessageId, record.MessageId, cancellationToken))
                    {
                        removed++;
                    }
                }
                else
                {
                    await TryDeleteRemoteSerialAsync(record.MessageId, mapping.SerialMessageId, cancellationToken);
                    removed++;
                }
            }
        }

        return removed;
    }

    public IReadOnlyList<AppEntry> MaterializeEntries(
        FolderSerial serial,
        IReadOnlyList<AppEntry> cachedEntries,
        string folderPath,
        IReadOnlyDictionary<string, FolderIndexMapping> mappings,
        bool preserveChildParentFolderId = false)
    {
        var cachedByKey = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in cachedEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.FileId)) cachedByKey["file:" + entry.FileId] = entry;
            if (!string.IsNullOrWhiteSpace(entry.FolderId)) cachedByKey["folder:" + entry.FolderId] = entry;
        }

        var result = new List<AppEntry>();
        foreach (var child in serial.Children)
        {
            if (string.IsNullOrWhiteSpace(child.Id))
            {
                continue;
            }

            var key = (child.IsFolder ? "folder:" : "file:") + child.Id;
            var entry = cachedByKey.TryGetValue(key, out var cached)
                ? CopyEntry(cached)
                : new AppEntry();
            if (!child.IsFolder && cached != null &&
                (cached.Encryption?.Salt != child.Encryption?.Salt || cached.ThreadId != child.ThreadId ||
                 (child.SizeBytes > 0 && cached.SizeBytes != child.SizeBytes) || cached.PreviewMessageId != child.PreviewMessageId))
            {
                // A newer payload in the same thread must not reuse the previous chunk list/key context.
                entry.ChunkMessageIds = [];
                entry.ChunkReferences = null;
                entry.VideoPreviewChunkMessageIds = [];
                entry.VideoPreviewChunkReferences = null;
                entry.VideoPreviewMessageId = "";
                entry.VideoPreviewSizeBytes = 0;
                entry.ChunkCount = 0;
                entry.BatchId = "";
                entry.VersionId = "";
                entry.VersionUploadedAt = "";
                entry.ModifiedAt = "";
                entry.Versions = [];
                entry.SizeBytes = 0;
            }
            entry.Name = child.Name;
            entry.MetadataMessageId = child.NamesMessageId;
            // A serial contains only direct children of its own folder. Use
            // that relationship even when an older serial stored a stale
            // parentFolderId.
            entry.ParentFolderId = preserveChildParentFolderId ? child.ParentFolderId : serial.FolderId;
            entry.Path = PathRules.Normalize(folderPath);
            entry.Type = child.IsFolder ? "folder" : "file";
            entry.FileId = child.IsFolder ? "" : child.Id;
            entry.FolderId = child.IsFolder ? child.Id : "";
            entry.ThreadId = child.IsFolder && mappings.TryGetValue(child.Id, out var mapping)
                ? mapping.ThreadId
                : child.ThreadId;
            entry.UploadedAt = child.UploadedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            entry.SizeBytes = child.IsFolder || child.SizeBytes > 0 ? Math.Max(0, child.SizeBytes) : entry.SizeBytes;
            entry.MimeType = child.MimeType;
            entry.PreviewMessageId = child.PreviewMessageId;
            entry.Encryption = child.Encryption?.Clone();
            entry.FolderColor = child.IsFolder ? child.FolderColor : "";
            entry.DeviceType = child.DeviceType;
            entry.Favorite = child.Favorite;
            entry.Normalize();
            result.Add(entry);
        }

        return result;
    }

    private async Task<FolderSerial?> ReadCachedFolderAsync(string folderId, CancellationToken cancellationToken)
    {
        if (_loadedFolders.TryGetValue(folderId, out var loaded))
        {
            return loaded;
        }

        var file = CacheFile(folderId);
        if (!file.Exists)
        {
            return null;
        }

        try
        {
            var serial = _codec.DecodeFolder(_transport.Decode(await File.ReadAllBytesAsync(file.FullName, cancellationToken)));
            if (!serial.FolderId.Equals(folderId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _loadedFolders[folderId] = serial;
            return serial;
        }
        catch (MetadataCodecException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            AppLogger.Error("Serial.CacheRead", ex, folderId);
            return null;
        }
    }

    private async Task WriteCachedAsync(string folderId, byte[] bytes, CancellationToken cancellationToken)
    {
        _cacheDirectory.Create();
        var target = CacheFile(folderId);
        var temporary = new FileInfo(target.FullName + ".tmp");
        await File.WriteAllBytesAsync(temporary.FullName, bytes, cancellationToken);
        try
        {
            File.Move(temporary.FullName, target.FullName, true);
        }
        catch
        {
            try { temporary.Delete(); } catch { }
            throw;
        }
    }

    private async Task<FileInfo> WriteTemporarySerialAsync(string folderId, byte[] bytes, CancellationToken cancellationToken)
    {
        _cacheDirectory.Create();
        var directory = _cacheDirectory.CreateSubdirectory("upload_" + Guid.NewGuid().ToString("N"));
        var file = new FileInfo(Path.Combine(directory.FullName, "serial.bin"));
        await File.WriteAllBytesAsync(file.FullName, bytes, cancellationToken);
        return file;
    }

    private FileInfo CacheFile(string folderId)
    {
        return new FileInfo(Path.Combine(_cacheDirectory.FullName, "serial_" + CacheToken(folderId) + ".bin"));
    }

    private static string CacheToken(string folderId)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folderId))).ToLowerInvariant();
    }

    private static FolderSerialEntry ToSerialEntry(
        AppEntry entry,
        IReadOnlyDictionary<string, FolderIndexMapping> mappings)
    {
        var isFolder = entry.Type.Equals("folder", StringComparison.OrdinalIgnoreCase);
        var threadId = isFolder && mappings.TryGetValue(entry.FolderId, out var mapping)
            ? mapping.ThreadId
            : entry.ThreadId;
        return new FolderSerialEntry
        {
            Id = isFolder ? entry.FolderId : entry.FileId,
            NamesMessageId = entry.MetadataMessageId,
            ParentFolderId = string.IsNullOrWhiteSpace(entry.ParentFolderId) ? RootFolderId : entry.ParentFolderId,
            Name = entry.Name,
            ThreadId = threadId,
            IsFolder = isFolder,
            MimeType = entry.MimeType,
            UploadedAt = ParseTimestamp(entry.UploadedAt),
            SizeBytes = Math.Max(0, entry.SizeBytes),
            PreviewMessageId = entry.PreviewMessageId,
            Encryption = isFolder ? null : entry.Encryption?.Clone(),
            FolderColor = isFolder && !string.IsNullOrWhiteSpace(entry.FolderColor) ? entry.FolderColor : "#C4C7C5",
            DeviceType = isFolder ? entry.DeviceType : null,
            Favorite = entry.Favorite
        };
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;
    }

    private static AppEntry CopyEntry(AppEntry source)
    {
        return new AppEntry
        {
            Name = source.Name,
            ThreadId = source.ThreadId,
            UploadedAt = source.UploadedAt,
            SizeBytes = source.SizeBytes,
            MetadataMessageId = source.MetadataMessageId,
            FileId = source.FileId,
            FolderId = source.FolderId,
            ParentFolderId = source.ParentFolderId,
            Path = source.Path,
            FolderColor = source.FolderColor,
            DeviceType = source.DeviceType,
            Sync = source.Sync,
            SyncDevice = source.SyncDevice,
            Favorite = source.Favorite,
            Type = source.Type,
            MimeType = source.MimeType,
            ChunkMessageIds = source.ChunkMessageIds.ToList(),
            ChunkReferences = source.ChunkReferences?.ToList(),
            VideoPreviewChunkMessageIds = source.VideoPreviewChunkMessageIds.ToList(),
            VideoPreviewChunkReferences = source.VideoPreviewChunkReferences?.ToList(),
            ChunkCount = source.ChunkCount,
            BatchId = source.BatchId,
            PreviewMessageId = source.PreviewMessageId,
            VideoPreviewMessageId = source.VideoPreviewMessageId,
            VideoPreviewSizeBytes = source.VideoPreviewSizeBytes,
            Encryption = source.Encryption?.Clone(),
            ModifiedAt = source.ModifiedAt,
            VersionId = source.VersionId,
            VersionUploadedAt = source.VersionUploadedAt,
            Versions = source.Versions
        };
    }

    private static bool IsImage(FolderSerialEntry entry)
    {
        return entry.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
               new[] { "jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "heif" }
                   .Contains(Path.GetExtension(entry.Name).TrimStart('.'), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsVideo(FolderSerialEntry entry)
    {
        return entry.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
               new[] { "mp4", "m4v", "mov", "webm", "mkv", "avi", "3gp" }
                   .Contains(Path.GetExtension(entry.Name).TrimStart('.'), StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record SerialMessageEnvelope(
    string FolderId,
    string FolderName,
    string Sha256)
{
    public const int SchemaVersion = 1;
    public const string EntryType = "folderSerial";
    private const int MaxDiscordContentLength = DiscordLimits.MessageContentCharacters;

    public static bool TryParse(string? content, out SerialMessageEnvelope envelope, IMetadataCodec? codec = null)
    {
        envelope = new SerialMessageEnvelope("", "", "");
        if (string.IsNullOrWhiteSpace(content)) return false;
        if (!content.TrimStart().StartsWith('{')) return false;
        try
        {
            using var document = JsonDocument.Parse((codec ?? MetadataCodec.Standard()).Decode(content));
            var root = document.RootElement;
            if (!root.TryGetProperty("schema", out var schema) ||
                schema.GetInt32() != SchemaVersion ||
                !root.TryGetProperty("entryType", out var type) ||
                !string.Equals(type.GetString(), EntryType, StringComparison.Ordinal))
            {
                return false;
            }

            var folderId = root.TryGetProperty("folderId", out var id) ? id.GetString()?.Trim() ?? "" : "";
            var folderName = root.TryGetProperty("folderName", out var name) ? name.GetString() ?? "" : "";
            var hash = root.TryGetProperty("sha256", out var value) ? value.GetString()?.Trim().ToLowerInvariant() ?? "" : "";
            if (folderId.Length == 0 || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            {
                return false;
            }

            envelope = new SerialMessageEnvelope(folderId, folderName, hash);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static string Encode(string folderId, string folderName, string sha256)
    {
        var payload = new Dictionary<string, object?>
        {
            ["schema"] = SchemaVersion,
            ["entryType"] = EntryType,
            ["folderId"] = folderId,
            ["sha256"] = sha256
        };
        if (folderName.Length > 0) payload["folderName"] = folderName;
        return JsonSerializer.Serialize(payload, JsonOptions.Web);
    }

    public static string FolderNameForMessage(string folderId, string folderName, string sha256)
    {
        var candidate = folderName ?? "";
        while (Encode(folderId, candidate, sha256).Length > MaxDiscordContentLength && candidate.Length > 0)
        {
            candidate = candidate[..^1];
        }

        return candidate;
    }
}
