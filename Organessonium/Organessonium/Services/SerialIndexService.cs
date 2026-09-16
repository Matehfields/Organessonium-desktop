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
    private readonly DiscordApiClient _discord;
    private readonly AppSettings _settings;
    private readonly DirectoryInfo _cacheDirectory;
    private readonly FolderSerialCodec _codec = new();
    private readonly ConcurrentDictionary<string, FolderSerial> _loadedFolders = new(StringComparer.OrdinalIgnoreCase);

    public SerialIndexService(
        DiscordApiClient discord,
        SettingsStore store,
        AppSettings settings)
    {
        _discord = discord;
        _settings = settings;
        _cacheDirectory = store.SerialCacheDirectory;
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
            return serial;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Serial.LoadFolder", ex, mapping.FolderId);
            return null;
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
        serial = PreserveFavorites(serial, previous);
        if (previous != null && FolderSerialCodec.SameContent(previous, serial))
        {
            return mapping;
        }

        var bytes = _codec.EncodeFolder(serial);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await WriteCachedAsync(serial.FolderId, bytes, cancellationToken);
        _loadedFolders[serial.FolderId] = serial;
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId))
        {
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
        var file = CacheFile(mapping.FolderId);
        try { if (file.Exists) file.Delete(); } catch (Exception ex) { AppLogger.Error("Serial.DeleteCache", ex, file.FullName); }
        if (!string.IsNullOrWhiteSpace(_settings.SerialChannelId) && !string.IsNullOrWhiteSpace(mapping.SerialMessageId))
        {
            await _discord.DeleteMessageAsync(_settings.SerialChannelId, mapping.SerialMessageId, cancellationToken);
        }
    }

    public async Task DeleteSupersededRemoteSerialAsync(
        string previousMessageId,
        string currentMessageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId) ||
            string.IsNullOrWhiteSpace(previousMessageId) ||
            previousMessageId.Equals(currentMessageId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _discord.DeleteMessageAsync(_settings.SerialChannelId, previousMessageId, cancellationToken);
        }
        catch (DiscordApiException ex) when ((int)ex.StatusCode == 404)
        {
            // The mapping already points at the new serial.
        }
    }

    public IReadOnlyList<AppEntry> MaterializeEntries(
        FolderSerial serial,
        IReadOnlyList<AppEntry> cachedEntries,
        string folderPath,
        IReadOnlyDictionary<string, FolderIndexMapping> mappings)
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
            entry.ParentFolderId = string.IsNullOrWhiteSpace(child.ParentFolderId) ? serial.FolderId : child.ParentFolderId;
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
            FolderColor = isFolder && !string.IsNullOrWhiteSpace(entry.FolderColor) ? entry.FolderColor : "#C4C7C5"
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

    private static FolderSerial PreserveFavorites(FolderSerial serial, FolderSerial? previous)
    {
        if (previous == null || previous.Children.Count == 0)
        {
            return serial;
        }

        var previousFavorites = previous.Children
            .Where(child => child.Favorite)
            .ToDictionary(child => (child.IsFolder, child.Id));
        if (previousFavorites.Count == 0)
        {
            return serial;
        }

        return new FolderSerial
        {
            FolderId = serial.FolderId,
            ParentFolderId = serial.ParentFolderId,
            FolderName = serial.FolderName,
            GeneratedAt = serial.GeneratedAt,
            Aggregate = serial.Aggregate,
            Kind = serial.Kind,
            Children = serial.Children.Select(child => new FolderSerialEntry
            {
                Id = child.Id,
                NamesMessageId = child.NamesMessageId,
                ParentFolderId = child.ParentFolderId,
                Name = child.Name,
                ThreadId = child.ThreadId,
                IsFolder = child.IsFolder,
                MimeType = child.MimeType,
                UploadedAt = child.UploadedAt,
                PreviewMessageId = child.PreviewMessageId,
                Encryption = child.Encryption?.Clone(),
                FolderColor = child.FolderColor,
                Favorite = previousFavorites.ContainsKey((child.IsFolder, child.Id)) || child.Favorite
            }).ToList()
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
