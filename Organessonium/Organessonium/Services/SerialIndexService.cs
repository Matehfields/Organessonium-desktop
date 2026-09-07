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
    private const string SynchronizedFolderId = "synchronized";
    private const string SynchronizedFolderPath = "/@synchronized";
    private const int MaxDiscordContentLength = 2000;

    private readonly DiscordApiClient _discord;
    private readonly AppSettings _settings;
    private readonly DirectoryInfo _cacheDirectory;
    private readonly FolderSerialCodec _codec = new();

    public SerialIndexService(
        DiscordApiClient discord,
        SettingsStore store,
        AppSettings settings)
    {
        _discord = discord;
        _settings = settings;
        _cacheDirectory = store.SerialCacheDirectory;
    }

    public async Task<IReadOnlyList<AppEntry>> LoadFastEntriesAsync(
        IReadOnlyList<AppEntry> cachedEntries,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SerialChannelId))
        {
            return [];
        }

        var records = await ScanRemoteRecordsAsync(cancellationToken);
        var descriptors = records
            .Where(record => record.Attachment != null)
            .GroupBy(record => record.Envelope.FolderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var serials = new ConcurrentDictionary<string, FolderSerial>(StringComparer.OrdinalIgnoreCase);
        if (descriptors.Count > 0)
        {
            var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
            await Parallel.ForEachAsync(
                descriptors,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = workers
                },
                async (descriptor, token) =>
                {
                    try
                    {
                        var bytes = await ReadOrDownloadAsync(descriptor, token);
                        var serial = _codec.DecodeFolder(bytes);
                        if (!serial.FolderId.Equals(descriptor.Envelope.FolderId, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new FormatException("O ID do serial não corresponde ao conteúdo.");
                        }

                        serials[serial.FolderId] = serial;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Serial.Load", ex, descriptor.Envelope.FolderId);
                    }
                });
        }

        if (serials.Count == 0)
        {
            foreach (var serial in await LoadCachedSerialsAsync(cancellationToken))
            {
                serials.TryAdd(serial.FolderId, serial);
            }
        }

        return MaterializeEntries(serials.Values, cachedEntries);
    }

    public async Task SynchronizeAsync(
        IEnumerable<AppEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var channelId = _settings.SerialChannelId.Trim();
        if (channelId.Length == 0)
        {
            return;
        }

        var serials = await BuildStableSerialsAsync(entries, cancellationToken);
        var records = await ScanRemoteRecordsAsync(cancellationToken);
        var grouped = records
            .GroupBy(record => record.Envelope.FolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var currentIds = serials.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(record => !currentIds.Contains(record.Envelope.FolderId)))
        {
            await _discord.DeleteMessageAsync(channelId, record.MessageId, cancellationToken);
        }

        foreach (var pair in serials.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = _codec.EncodeFolder(pair.Value);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var folderName = SerialMessageEnvelope.FolderNameForMessage(pair.Key, pair.Value.FolderName, hash);
            var existing = grouped.TryGetValue(pair.Key, out var previous)
                ? previous
                : [];
            var unchanged = existing.FirstOrDefault(record =>
                record.Attachment != null &&
                record.Envelope.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase) &&
                record.Envelope.FolderName == folderName);

            if (unchanged == null)
            {
                var temporary = await WriteTemporarySerialAsync(pair.Key, bytes, cancellationToken);
                try
                {
                    await _discord.SendFileMessageAsync(
                        channelId,
                        SerialMessageEnvelope.Encode(pair.Key, folderName, hash),
                        temporary,
                        cancellationToken);
                }
                finally
                {
                    try { temporary.Delete(); } catch { }
                }
            }

            foreach (var record in existing)
            {
                if (unchanged == null || record.MessageId != unchanged.MessageId)
                {
                    await _discord.DeleteMessageAsync(channelId, record.MessageId, cancellationToken);
                }
            }

            await WriteCachedAsync(pair.Key, bytes, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<SerialRemoteRecord>> ScanRemoteRecordsAsync(
        CancellationToken cancellationToken)
    {
        var messages = await _discord.GetMessagesInParallelAsync(
            _settings.SerialChannelId,
            Math.Clamp(_settings.SearchWorkers, 1, 8),
            cancellationToken);
        var records = new List<SerialRemoteRecord>();
        foreach (var message in messages)
        {
            if (message.Author?.Bot != true || !SerialMessageEnvelope.TryParse(message.Content, out var envelope))
            {
                continue;
            }

            var attachment = message.Attachments.FirstOrDefault(candidate =>
                candidate.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
            records.Add(new SerialRemoteRecord(message.Id, envelope, attachment));
        }

        return records;
    }

    private async Task<byte[]> ReadOrDownloadAsync(
        SerialRemoteRecord descriptor,
        CancellationToken cancellationToken)
    {
        var cached = await ReadCachedAsync(descriptor.Envelope.FolderId, descriptor.Envelope.Sha256, cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        var attachment = descriptor.Attachment ?? throw new FormatException("Serial sem anexo binário.");
        var bytes = await _discord.DownloadAttachmentBytesAsync(attachment, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!hash.Equals(descriptor.Envelope.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A validação SHA-256 do serial falhou.");
        }

        _codec.DecodeFolder(bytes);
        await WriteCachedAsync(descriptor.Envelope.FolderId, bytes, cancellationToken);
        return bytes;
    }

    private async Task<IReadOnlyList<FolderSerial>> LoadCachedSerialsAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<FolderSerial>();
        foreach (var file in _cacheDirectory.EnumerateFiles("*.bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var serial = _codec.DecodeFolder(await File.ReadAllBytesAsync(file.FullName, cancellationToken));
                result.Add(serial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                AppLogger.Error("Serial.CacheRead", ex, file.FullName);
            }
        }

        return result;
    }

    private async Task<byte[]?> ReadCachedAsync(
        string serialId,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var file = CacheFile(serialId);
        if (!file.Exists)
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _codec.DecodeFolder(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            AppLogger.Error("Serial.CacheRead", ex, serialId);
            return null;
        }
    }

    private async Task WriteCachedAsync(
        string serialId,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var target = CacheFile(serialId);
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

    private async Task<FileInfo> WriteTemporarySerialAsync(
        string serialId,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(Path.Combine(
            _cacheDirectory.FullName,
            "upload_" + CacheToken(serialId) + "_" + Guid.NewGuid().ToString("N") + ".bin"));
        await File.WriteAllBytesAsync(file.FullName, bytes, cancellationToken);
        return file;
    }

    private FileInfo CacheFile(string serialId)
    {
        return new FileInfo(Path.Combine(_cacheDirectory.FullName, "serial_" + CacheToken(serialId) + ".bin"));
    }

    private static string CacheToken(string serialId)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialId))).ToLowerInvariant();
    }

    private IReadOnlyList<AppEntry> MaterializeEntries(
        IEnumerable<FolderSerial> source,
        IReadOnlyList<AppEntry> cachedEntries)
    {
        var serials = source
            .Where(serial => serial.Kind == FolderSerialKind.Folder && !string.IsNullOrWhiteSpace(serial.FolderId))
            .GroupBy(serial => serial.FolderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(serial => serial.FolderId, StringComparer.OrdinalIgnoreCase);
        var cachedByKey = BuildCachedLookup(cachedEntries);
        var result = new List<AppEntry>();
        var pathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string FolderPath(string folderId, HashSet<string>? visiting = null)
        {
            if (folderId.Equals(RootFolderId, StringComparison.OrdinalIgnoreCase)) return "/";
            if (folderId.Equals(SynchronizedFolderId, StringComparison.OrdinalIgnoreCase)) return SynchronizedFolderPath;
            if (pathCache.TryGetValue(folderId, out var known)) return known;
            if (!serials.TryGetValue(folderId, out var serial)) return "/";

            var active = visiting ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!active.Add(folderId)) return "/";
            try
            {
                var parent = string.IsNullOrWhiteSpace(serial.ParentFolderId) ? RootFolderId : serial.ParentFolderId;
                var parentPath = FolderPath(parent, active);
                var path = PathRules.Normalize(parentPath == "/"
                    ? "/" + serial.FolderName
                    : parentPath + "/" + serial.FolderName);
                pathCache[folderId] = path;
                return path;
            }
            finally
            {
                active.Remove(folderId);
            }
        }

        foreach (var serial in serials.Values.OrderBy(serial => FolderPath(serial.FolderId), StringComparer.OrdinalIgnoreCase))
        {
            var parentPath = FolderPath(serial.FolderId);
            foreach (var child in serial.Children)
            {
                var summary = ToSummary(child, parentPath);
                if (summary == null) continue;
                result.Add(Hydrate(summary, cachedByKey));
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, FolderSerial>> BuildStableSerialsAsync(
        IEnumerable<AppEntry> source,
        CancellationToken cancellationToken)
    {
        var candidates = BuildSerials(source);
        var stable = new Dictionary<string, FolderSerial>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in candidates)
        {
            var previous = await ReadCachedSerialAsync(pair.Key, cancellationToken);
            stable[pair.Key] = previous != null && FolderSerialCodec.SameContent(previous, pair.Value)
                ? CopyWithGeneratedAt(pair.Value, previous.GeneratedAt)
                : pair.Value;
        }

        return stable;
    }

    private async Task<FolderSerial?> ReadCachedSerialAsync(
        string serialId,
        CancellationToken cancellationToken)
    {
        var file = CacheFile(serialId);
        if (!file.Exists) return null;
        try
        {
            return _codec.DecodeFolder(await File.ReadAllBytesAsync(file.FullName, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            AppLogger.Error("Serial.CacheRead", ex, serialId);
            return null;
        }
    }

    private IReadOnlyDictionary<string, FolderSerial> BuildSerials(IEnumerable<AppEntry> source)
    {
        var entries = source
            .Where(entry => entry != null)
            .ToList();
        var folders = entries
            .Where(entry => entry.Type.Equals("folder", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.FolderId))
            .ToDictionary(entry => entry.FolderId, StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<AppEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var id = string.IsNullOrWhiteSpace(entry.ParentFolderId) ||
                     entry.ParentFolderId.Equals(RootFolderId, StringComparison.OrdinalIgnoreCase)
                ? RootFolderId
                : entry.ParentFolderId;
            if (!children.TryGetValue(id, out var list))
            {
                list = [];
                children[id] = list;
            }

            list.Add(entry);
        }

        var aggregateCache = new Dictionary<string, FolderSerialAggregate>(StringComparer.OrdinalIgnoreCase);
        FolderSerialAggregate AggregateFor(string folderId, HashSet<string>? ancestors = null)
        {
            if (aggregateCache.TryGetValue(folderId, out var cached)) return cached;
            var active = ancestors ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!active.Add(folderId)) throw new InvalidOperationException("Ciclo de pastas encontrado no índice serial.");
            try
            {
                long fileCount = 0;
                long folderCount = 0;
                long totalBytes = 0;
                long imageCount = 0;
                long videoCount = 0;
                var typeCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in children.GetValueOrDefault(folderId) ?? [])
                {
                    if (entry.Type.Equals("folder", StringComparison.OrdinalIgnoreCase))
                    {
                        folderCount++;
                        var nested = AggregateFor(entry.FolderId, new HashSet<string>(active, StringComparer.OrdinalIgnoreCase));
                        fileCount += nested.FileCount;
                        folderCount += nested.FolderCount;
                        totalBytes += nested.TotalBytes;
                        imageCount += nested.ImageCount;
                        videoCount += nested.VideoCount;
                        foreach (var type in nested.TypeCounts)
                        {
                            typeCounts[type.Key] = typeCounts.GetValueOrDefault(type.Key) + type.Value;
                        }
                    }
                    else
                    {
                        fileCount++;
                        totalBytes += Math.Max(0, entry.SizeBytes);
                        var extension = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();
                        if (extension.Length > 0) typeCounts[extension] = typeCounts.GetValueOrDefault(extension) + 1;
                        if (IsImage(entry)) imageCount++;
                        if (IsVideo(entry)) videoCount++;
                    }
                }

                var aggregate = new FolderSerialAggregate
                {
                    TotalItems = fileCount + folderCount,
                    FileCount = fileCount,
                    FolderCount = folderCount,
                    TotalBytes = totalBytes,
                    ImageCount = imageCount,
                    VideoCount = videoCount,
                    TypeCounts = typeCounts
                };
                aggregateCache[folderId] = aggregate;
                return aggregate;
            }
            finally
            {
                active.Remove(folderId);
            }
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var result = new Dictionary<string, FolderSerial>(StringComparer.OrdinalIgnoreCase)
        {
            [RootFolderId] = new FolderSerial
            {
                FolderId = RootFolderId,
                ParentFolderId = RootFolderId,
                FolderName = RootFolderId,
                GeneratedAt = generatedAt,
                Aggregate = AggregateFor(RootFolderId),
                Children = ToSerialChildren(children.GetValueOrDefault(RootFolderId) ?? [])
            }
        };

        foreach (var folder in folders.Values)
        {
            var parentId = string.IsNullOrWhiteSpace(folder.ParentFolderId) ||
                           folder.ParentFolderId.Equals(RootFolderId, StringComparison.OrdinalIgnoreCase)
                ? RootFolderId
                : folder.ParentFolderId;
            result[folder.FolderId] = new FolderSerial
            {
                FolderId = folder.FolderId,
                ParentFolderId = parentId,
                FolderName = folder.Name,
                GeneratedAt = generatedAt,
                Aggregate = AggregateFor(folder.FolderId),
                Children = ToSerialChildren(children.GetValueOrDefault(folder.FolderId) ?? [])
            };
        }

        return result;

        List<FolderSerialEntry> ToSerialChildren(IEnumerable<AppEntry> values)
        {
            return values
                .Where(entry => entry.Type.Equals("folder", StringComparison.OrdinalIgnoreCase)
                    ? !string.IsNullOrWhiteSpace(entry.FolderId)
                    : !string.IsNullOrWhiteSpace(entry.FileId))
                .OrderByDescending(entry => entry.Type.Equals("folder", StringComparison.OrdinalIgnoreCase))
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Type.Equals("folder", StringComparison.OrdinalIgnoreCase) ? entry.FolderId : entry.FileId, StringComparer.Ordinal)
                .Select(ToSerialEntry)
                .ToList();
        }

        FolderSerialEntry ToSerialEntry(AppEntry entry)
        {
            var isFolder = entry.Type.Equals("folder", StringComparison.OrdinalIgnoreCase);
            return new FolderSerialEntry
            {
                Id = isFolder ? entry.FolderId : entry.FileId,
                NamesMessageId = entry.MetadataMessageId,
                ParentFolderId = string.IsNullOrWhiteSpace(entry.ParentFolderId) ? RootFolderId : entry.ParentFolderId,
                Name = entry.Name,
                ThreadId = string.IsNullOrWhiteSpace(entry.ThreadId)
                    ? (isFolder ? "folder_" + entry.FolderId : entry.ThreadId)
                    : entry.ThreadId,
                IsFolder = isFolder,
                MimeType = entry.MimeType,
                UploadedAt = ParseTimestamp(entry.UploadedAt),
                PreviewMessageId = entry.PreviewMessageId,
                Encryption = isFolder ? null : entry.Encryption?.Clone(),
                FolderColor = isFolder && !string.IsNullOrWhiteSpace(entry.FolderColor)
                    ? entry.FolderColor
                    : "#C4C7C5"
            };
        }
    }

    private static AppEntry? ToSummary(FolderSerialEntry child, string path)
    {
        if (string.IsNullOrWhiteSpace(child.Id)) return null;
        var isFolder = child.IsFolder;
        var entry = new AppEntry
        {
            Name = child.Name,
            ThreadId = string.IsNullOrWhiteSpace(child.ThreadId)
                ? (isFolder ? "folder_" + child.Id : "")
                : child.ThreadId,
            UploadedAt = child.UploadedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            SizeBytes = 0,
            MetadataMessageId = child.NamesMessageId,
            FileId = isFolder ? "" : child.Id,
            FolderId = isFolder ? child.Id : "",
            ParentFolderId = string.IsNullOrWhiteSpace(child.ParentFolderId) ? RootFolderId : child.ParentFolderId,
            Path = path,
            FolderColor = isFolder ? child.FolderColor : "",
            Type = isFolder ? "folder" : "file",
            MimeType = child.MimeType,
            PreviewMessageId = child.PreviewMessageId,
            Encryption = child.Encryption?.Clone()
        };
        entry.Normalize();
        return entry;
    }

    private static AppEntry Hydrate(AppEntry summary, IReadOnlyDictionary<string, AppEntry> cachedByKey)
    {
        if (!cachedByKey.TryGetValue(EntryKey(summary), out var cached))
        {
            return summary;
        }

        var hydrated = CopyEntry(cached);
        hydrated.Name = summary.Name;
        hydrated.ThreadId = summary.ThreadId;
        hydrated.UploadedAt = summary.UploadedAt;
        hydrated.MetadataMessageId = summary.MetadataMessageId;
        hydrated.FileId = summary.FileId;
        hydrated.FolderId = summary.FolderId;
        hydrated.ParentFolderId = summary.ParentFolderId;
        hydrated.Path = summary.Path;
        hydrated.Type = summary.Type;
        hydrated.MimeType = string.IsNullOrWhiteSpace(summary.MimeType) ? cached.MimeType : summary.MimeType;
        hydrated.FolderColor = string.IsNullOrWhiteSpace(summary.FolderColor) ? cached.FolderColor : summary.FolderColor;
        hydrated.PreviewMessageId = string.IsNullOrWhiteSpace(summary.PreviewMessageId)
            ? cached.PreviewMessageId
            : summary.PreviewMessageId;
        hydrated.Encryption = summary.Encryption?.Clone() ?? cached.Encryption?.Clone();
        hydrated.Normalize();
        return hydrated;
    }

    private static IReadOnlyDictionary<string, AppEntry> BuildCachedLookup(IEnumerable<AppEntry> entries)
    {
        var lookup = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            foreach (var key in EntryKeys(entry))
            {
                lookup.TryAdd(key, entry);
            }
        }

        return lookup;
    }

    private static IEnumerable<string> EntryKeys(AppEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.MetadataMessageId)) yield return "metadata:" + entry.MetadataMessageId;
        if (!string.IsNullOrWhiteSpace(entry.FileId)) yield return "file:" + entry.FileId;
        if (!string.IsNullOrWhiteSpace(entry.FolderId)) yield return "folder:" + entry.FolderId;
        if (!string.IsNullOrWhiteSpace(entry.ThreadId)) yield return "thread:" + entry.ThreadId;
    }

    private static string EntryKey(AppEntry entry)
    {
        return entry.Type.Equals("folder", StringComparison.OrdinalIgnoreCase)
            ? "folder:" + entry.FolderId
            : "file:" + entry.FileId;
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

    private static FolderSerial CopyWithGeneratedAt(FolderSerial source, DateTimeOffset generatedAt)
    {
        return new FolderSerial
        {
            FolderId = source.FolderId,
            ParentFolderId = source.ParentFolderId,
            FolderName = source.FolderName,
            GeneratedAt = generatedAt,
            Aggregate = source.Aggregate,
            Children = source.Children,
            Kind = source.Kind
        };
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.UnixEpoch;
    }

    private static bool IsImage(AppEntry entry)
    {
        return entry.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
               new[] { "jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "heif" }
                   .Contains(Path.GetExtension(entry.Name).TrimStart('.'), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsVideo(AppEntry entry)
    {
        return entry.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
               new[] { "mp4", "m4v", "mov", "webm", "mkv", "avi", "3gp" }
                   .Contains(Path.GetExtension(entry.Name).TrimStart('.'), StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record SerialRemoteRecord(
    string MessageId,
    SerialMessageEnvelope Envelope,
    DiscordAttachmentDto? Attachment);

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

            var folderId = root.TryGetProperty("folderId", out var id)
                ? id.GetString()?.Trim() ?? ""
                : "";
            var folderName = root.TryGetProperty("folderName", out var name)
                ? name.GetString() ?? ""
                : "";
            var hash = root.TryGetProperty("sha256", out var value)
                ? value.GetString()?.Trim().ToLowerInvariant() ?? ""
                : "";
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
