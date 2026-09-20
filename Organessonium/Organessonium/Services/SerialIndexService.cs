using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class SerialIndexService
{
    private const string RootFolderId = "root";
    public const string FavoritesSerialId = "favorites";
    private readonly DiscordApiClient _discord;
    private readonly AppSettings _settings;
    private readonly DirectoryInfo _cacheDirectory;
    private readonly FolderSerialCodec _codec = new();
    private readonly ConcurrentDictionary<string, FolderSerial> _loadedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _verifiedRemoteSerialIds = new(StringComparer.OrdinalIgnoreCase);

    public SerialIndexService(
        DiscordApiClient discord,
        SettingsStore store,
        AppSettings settings)
        : this(discord, settings, store.SerialCacheDirectory)
    {
    }

    public SerialIndexService(
        DiscordApiClient discord,
        AppSettings settings,
        DirectoryInfo cacheDirectory)
    {
        _discord = discord;
        _settings = settings;
        _cacheDirectory = cacheDirectory;
    }

    public Task<FolderSerial?> LoadCachedFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        return ReadCachedFolderAsync(folderId, cancellationToken);
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
                throw new InvalidDataException("A validação SHA-256 do serial falhou.");
            }

            var serial = _codec.DecodeFolder(bytes);
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
        catch (Exception ex)
        {
            AppLogger.Error("Serial.LoadFolder", ex, mapping.FolderId);
            return null;
        }
    }

    public async Task<FolderSerial?> LoadStandaloneSerialAsync(
        string serialId,
        FolderSerialKind expectedKind,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = serialId.Trim();
        if (normalizedId.Length == 0 || string.IsNullOrWhiteSpace(_settings.SerialChannelId))
        {
            return null;
        }

        var messages = await _discord.GetMessagesInParallelAsync(
            _settings.SerialChannelId,
            Math.Clamp(_settings.SearchWorkers, 1, 8),
            cancellationToken);
        var candidates = messages
            .Where(message => SerialMessageEnvelope.TryParse(message.Content, out var envelope) &&
                              envelope.FolderId.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(message => ulong.TryParse(message.Id, out var id) ? id : 0)
            .ToList();
        if (candidates.Count == 0)
        {
            await InvalidateCachedFolderAsync(normalizedId);
            return null;
        }

        foreach (var message in candidates)
        {
            var attachment = message.Attachments.FirstOrDefault(candidate =>
                candidate.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
            if (attachment == null || !SerialMessageEnvelope.TryParse(message.Content, out var envelope))
            {
                continue;
            }

            try
            {
                var bytes = await _discord.DownloadAttachmentBytesAsync(attachment, cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!hash.Equals(envelope.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var serial = _codec.DecodeFolder(bytes);
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
            catch (Exception ex)
            {
                AppLogger.Error("Serial.LoadStandalone", ex, normalizedId);
            }
        }

        return await ReadCachedFolderAsync(normalizedId, cancellationToken) is { } cached && cached.Kind == expectedKind
            ? cached
            : null;
    }

    public async Task SaveStandaloneSerialAsync(
        FolderSerial serial,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId))
        {
            return;
        }

        var bytes = _codec.EncodeFolder(serial);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var messages = await _discord.GetMessagesInParallelAsync(
            _settings.SerialChannelId,
            Math.Clamp(_settings.SearchWorkers, 1, 8),
            cancellationToken);
        var existing = messages
            .Where(message => SerialMessageEnvelope.TryParse(message.Content, out var envelope) &&
                              envelope.FolderId.Equals(serial.FolderId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (serial.Children.Count == 0)
        {
            foreach (var message in existing)
            {
                await TryDeleteRemoteSerialAsync(message.Id, cancellationToken: cancellationToken);
            }
            await InvalidateCachedFolderAsync(serial.FolderId);
            return;
        }

        var unchanged = existing.FirstOrDefault(message =>
            SerialMessageEnvelope.TryParse(message.Content, out var envelope) &&
            envelope.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));

        string preservedMessageId;
        if (unchanged != null)
        {
            preservedMessageId = unchanged.Id;
        }
        else
        {
            var temporary = await WriteTemporarySerialAsync(serial.FolderId, bytes, cancellationToken);
            try
            {
                var folderName = SerialMessageEnvelope.FolderNameForMessage(serial.FolderId, serial.FolderName, hash);
                var sent = await _discord.SendFileMessageAsync(
                    _settings.SerialChannelId,
                    SerialMessageEnvelope.Encode(serial.FolderId, folderName, hash),
                    temporary,
                    cancellationToken);
                preservedMessageId = sent.Id;
            }
            finally
            {
                try { temporary.Delete(); } catch { }
            }
        }

        await WriteCachedAsync(serial.FolderId, bytes, cancellationToken);
        _loadedFolders[serial.FolderId] = serial;
        foreach (var duplicate in existing.Where(message => message.Id != preservedMessageId))
        {
            await TryDeleteRemoteSerialAsync(duplicate.Id, preservedMessageId, cancellationToken);
        }
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
        var requiresRemoteUpload = await RequiresRemoteUploadAsync(mapping, cancellationToken);
        if (previous != null &&
            FolderSerialCodec.SameContent(previous, serial) &&
            !requiresRemoteUpload)
        {
            return mapping;
        }

        var bytes = _codec.EncodeFolder(serial);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await WriteCachedAsync(serial.FolderId, bytes, cancellationToken);
        _loadedFolders[serial.FolderId] = serial;
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId))
        {
            _verifiedRemoteSerialIds.TryRemove(serial.FolderId, out _);
            return mapping with { SerialMessageId = "", SerialSha256 = "" };
        }

        var temporary = await WriteTemporarySerialAsync(serial.FolderId, bytes, cancellationToken);
        try
        {
            var folderName = SerialMessageEnvelope.FolderNameForMessage(serial.FolderId, serial.FolderName, hash);
            var message = await _discord.SendFileMessageAsync(
                _settings.SerialChannelId,
                SerialMessageEnvelope.Encode(serial.FolderId, folderName, hash),
                temporary,
                cancellationToken);
            _verifiedRemoteSerialIds[serial.FolderId] = message.Id;
            return mapping with { SerialMessageId = message.Id, SerialSha256 = hash };
        }
        finally
        {
            try { temporary.Delete(); } catch { }
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
                !SerialMessageEnvelope.TryParse(message.Content, out var envelope))
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
            var serial = _codec.DecodeFolder(await File.ReadAllBytesAsync(file.FullName, cancellationToken));
            if (!serial.FolderId.Equals(folderId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _loadedFolders[folderId] = serial;
            return serial;
        }
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
        var file = new FileInfo(Path.Combine(
            _cacheDirectory.FullName,
            "upload_" + CacheToken(folderId) + "_" + Guid.NewGuid().ToString("N") + ".bin"));
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
            Favorite = source.Favorite,
            Type = source.Type,
            MimeType = source.MimeType,
            ChunkMessageIds = source.ChunkMessageIds.ToList(),
            VideoPreviewChunkMessageIds = source.VideoPreviewChunkMessageIds.ToList(),
            ChunkCount = source.ChunkCount,
            BatchId = source.BatchId,
            PreviewMessageId = source.PreviewMessageId,
            VideoPreviewMessageId = source.VideoPreviewMessageId,
            VideoPreviewSizeBytes = source.VideoPreviewSizeBytes,
            Encryption = source.Encryption?.Clone()
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
    private const int MaxDiscordContentLength = 2000;

    public static bool TryParse(string? content, out SerialMessageEnvelope envelope)
    {
        envelope = new SerialMessageEnvelope("", "", "");
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
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
