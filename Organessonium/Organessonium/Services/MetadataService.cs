using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class MetadataService
{
    public const int SchemaVersion = 2;
    public const string FileName = "metadata.json";
    public const int InlineContentLimit = 2000;
    public const long MaxMetadataBytes = 10L * 1024 * 1024;
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonOptions.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions AttachmentJsonOptions = new(JsonOptions.WebIndented)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<MetadataWritePayload> CreateWritePayloadAsync(AppEntry entry, CancellationToken cancellationToken = default)
    {
        var metadata = CreateMetadata(entry);
        var compactJson = SerializeMetadata(metadata, CompactJsonOptions);
        if (compactJson.Length <= InlineContentLimit)
        {
            return new MetadataWritePayload(compactJson, null);
        }

        return new MetadataWritePayload("", await CreateMetadataFileAsync(metadata, entry.Name, cancellationToken));
    }

    public async Task<string> SendEntryAsync(
        DiscordApiClient discord,
        string namesChannelId,
        AppEntry entry,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        entry.Normalize();
        var payload = await CreateWritePayloadAsync(entry, cancellationToken);
        try
        {
            var message = payload.Attachment == null
                ? await discord.CreateMessageAsync(
                    namesChannelId,
                    payload.Content,
                    cancellationToken,
                    onRateLimitWait)
                : await SendMetadataFileAsync(
                    discord,
                    namesChannelId,
                    payload.Attachment,
                    cancellationToken,
                    onRateLimitWait);
            entry.MetadataMessageId = message.Id;
            return message.Id;
        }
        finally
        {
            try { payload.Attachment?.Directory?.Delete(true); } catch { }
        }
    }

    private static async Task<DiscordMessageDto> SendMetadataFileAsync(
        DiscordApiClient discord,
        string namesChannelId,
        FileInfo file,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait)
    {
        try
        {
            return await discord.SendFileMessageAsync(
                namesChannelId,
                "",
                file,
                cancellationToken,
                onRateLimitWait);
        }
        catch (DiscordApiException ex) when ((int)ex.StatusCode == 400)
        {
            return await discord.SendFileMessageAsync(
                namesChannelId,
                "metadata",
                file,
                cancellationToken,
                onRateLimitWait);
        }
    }

    public async Task DeleteEntryAsync(
        DiscordApiClient discord,
        string namesChannelId,
        AppEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(entry.MetadataMessageId))
        {
            await discord.DeleteMessageAsync(namesChannelId, entry.MetadataMessageId, cancellationToken);
        }
    }

    public async Task<FileInfo> CreateMetadataFileAsync(AppEntry entry, CancellationToken cancellationToken = default)
    {
        var metadata = CreateMetadata(entry);
        return await CreateMetadataFileAsync(metadata, entry.Name, cancellationToken);
    }

    private static async Task<FileInfo> CreateMetadataFileAsync(
        MetadataEntry metadata,
        string details,
        CancellationToken cancellationToken)
    {
        var json = SerializeMetadata(metadata, AttachmentJsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxMetadataBytes)
        {
            AppLogger.Error("Metadata.TooLarge", new InvalidOperationException($"{bytes} bytes"), details);
            throw new InvalidOperationException("metadata.json ficou grande demais para ser enviado como anexo.");
        }

        var dir = Directory.CreateTempSubdirectory("organessonium_metadata_");
        var file = new FileInfo(Path.Combine(dir.FullName, FileName));
        await File.WriteAllTextAsync(file.FullName, json, cancellationToken);
        file.Refresh();
        return file;
    }

    public async Task<MetadataEnvelope?> ReadEnvelopeAsync(
        DiscordMessageDto message,
        CancellationToken cancellationToken = default)
    {
        var inline = TryReadInlineEnvelope(message);
        if (inline != null)
        {
            return inline;
        }

        var attachment = message.Attachments.FirstOrDefault(IsMetadataAttachment);
        if (attachment == null)
        {
            return null;
        }

        if (attachment.Size > MaxMetadataBytes)
        {
            AppLogger.Error("Metadata.ReadTooLarge", new InvalidOperationException($"{attachment.Size} bytes"), message.Id);
            return null;
        }

        try
        {
            using var http = NetworkConfig.Current.CreateClient();
            using var response = await http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var metadata = ParseMetadata(document.RootElement);
            if (metadata == null)
            {
                return null;
            }

            Validate(metadata);
            return new MetadataEnvelope(message.Id, metadata);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Metadata.ReadInvalid", ex, message.Id);
            return null;
        }
    }

    private static MetadataEnvelope? TryReadInlineEnvelope(DiscordMessageDto message)
    {
        var content = message.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content) || !content.StartsWith("{", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var metadata = ParseMetadata(document.RootElement);
            if (metadata == null)
            {
                return null;
            }

            Validate(metadata);
            return new MetadataEnvelope(message.Id, metadata);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Metadata.ReadInlineInvalid", ex, message.Id);
            return null;
        }
    }

    public async Task<IReadOnlyList<MetadataEnvelope>> ReadEnvelopesInParallelAsync(
        IReadOnlyList<DiscordMessageDto> messages,
        int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        using var semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
        var results = new MetadataEnvelope?[messages.Count];
        var tasks = new Task[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            var index = i;
            tasks[index] = ReadEnvelopeTrackedAsync(messages[index], results, index, semaphore, cancellationToken);
        }

        await Task.WhenAll(tasks);
        return results
            .Where(envelope => envelope != null)
            .Cast<MetadataEnvelope>()
            .ToList();
    }

    private async Task ReadEnvelopeTrackedAsync(
        DiscordMessageDto message,
        MetadataEnvelope?[] results,
        int index,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            results[index] = await ReadEnvelopeAsync(message, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public List<AppEntry> ToAppEntries(IReadOnlyList<MetadataEnvelope> envelopes)
    {
        var entries = envelopes
            .Select(ToAppEntry)
            .Where(entry => entry != null)
            .Cast<AppEntry>()
            .ToList();
        ResolvePaths(entries);
        return entries;
    }

    public AppEntry? ToAppEntry(MetadataEnvelope envelope)
    {
        AppEntry entry;
        switch (envelope.Entry)
        {
            case FileMetadata file:
                entry = AppEntry.File(file.Name, file.ThreadId, file.UploadedAt, file.SizeBytes, "/");
                entry.FileId = file.FileId;
                entry.ParentFolderId = NormalizeParentId(file.ParentFolderId);
                entry.MimeType = file.MimeType;
                entry.BatchId = file.BatchId;
                entry.ChunkMessageIds = file.Chunks.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
                entry.ChunkCount = entry.ChunkMessageIds.Count;
                entry.PreviewMessageId = file.Preview?.ThumbnailMessageId ?? "";
                entry.VideoPreviewChunkMessageIds = file.Preview?.Video480p?.Chunks
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList() ?? [];
                entry.VideoPreviewMessageId = entry.VideoPreviewChunkMessageIds.FirstOrDefault() ?? "";
                entry.VideoPreviewSizeBytes = Math.Max(0, file.Preview?.Video480p?.SizeBytes ?? 0);
                entry.Encryption = file.Encryption?.Clone();
                break;
            case FolderMetadata folder:
                entry = AppEntry.Folder(folder.Name, "/");
                entry.FolderId = folder.FolderId;
                entry.ThreadId = string.IsNullOrWhiteSpace(folder.ThreadId)
                    ? "folder_" + folder.FolderId[..Math.Min(16, folder.FolderId.Length)]
                    : folder.ThreadId!;
                entry.UploadedAt = folder.CreatedAt;
                entry.ParentFolderId = NormalizeParentId(folder.ParentFolderId);
                entry.FolderColor = string.IsNullOrWhiteSpace(folder.FolderColor) ? "#C4C7C5" : folder.FolderColor;
                break;
            default:
                return null;
        }

        entry.MetadataMessageId = envelope.MessageId;
        entry.Normalize();
        return entry;
    }

    private static FileMetadata CreateFileMetadata(AppEntry entry)
    {
        return new FileMetadata
        {
            SchemaVersion = SchemaVersion,
            FileId = string.IsNullOrWhiteSpace(entry.FileId) ? Guid.NewGuid().ToString("N") : entry.FileId,
            ThreadId = entry.ThreadId,
            Name = entry.Name,
            MimeType = entry.MimeType,
            SizeBytes = entry.SizeBytes,
            UploadedAt = NormalizeIsoTimestamp(entry.UploadedAt),
            BatchId = entry.BatchId,
            ParentFolderId = NormalizeParentId(entry.ParentFolderId),
            Chunks = entry.ChunkMessageIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList(),
            Preview = CreatePreviewMetadata(entry),
            Encryption = entry.Encryption?.Clone()
        };
    }

    private static PreviewMetadata? CreatePreviewMetadata(AppEntry entry)
    {
        var thumbnailMessageId = string.IsNullOrWhiteSpace(entry.PreviewMessageId) ? null : entry.PreviewMessageId;
        var videoChunks = entry.VideoPreviewChunkMessageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (videoChunks.Count == 0 && !string.IsNullOrWhiteSpace(entry.VideoPreviewMessageId))
        {
            videoChunks.Add(entry.VideoPreviewMessageId);
        }

        VideoPreviewMetadata? video480p = null;
        if (videoChunks.Count > 0 && entry.VideoPreviewSizeBytes > 0)
        {
            video480p = new VideoPreviewMetadata
            {
                SizeBytes = entry.VideoPreviewSizeBytes,
                Chunks = videoChunks
            };
        }

        if (thumbnailMessageId == null && video480p == null)
        {
            return null;
        }

        return new PreviewMetadata
        {
            ThumbnailMessageId = thumbnailMessageId,
            Video480p = video480p
        };
    }

    private static MetadataEntry CreateMetadata(AppEntry entry)
    {
        entry.Normalize();
        MetadataEntry metadata = entry.Type == "folder"
            ? CreateFolderMetadata(entry)
            : CreateFileMetadata(entry);
        Validate(metadata);
        return metadata;
    }

    private static string SerializeMetadata(MetadataEntry metadata, JsonSerializerOptions options)
    {
        return JsonSerializer.Serialize(metadata, metadata.GetType(), options);
    }

    private static MetadataEntry? ParseMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("schemaVersion", out var version) ||
            !version.TryGetInt32(out var parsedVersion) ||
            parsedVersion != SchemaVersion ||
            !root.TryGetProperty("entryType", out var entryTypeElement))
        {
            return null;
        }

        return entryTypeElement.GetString() switch
        {
            "file" => root.Deserialize<FileMetadata>(JsonOptions.Web),
            "folder" => root.Deserialize<FolderMetadata>(JsonOptions.Web),
            _ => null
        };
    }

    private static FolderMetadata CreateFolderMetadata(AppEntry entry)
    {
        return new FolderMetadata
        {
            SchemaVersion = SchemaVersion,
            FolderId = string.IsNullOrWhiteSpace(entry.FolderId) ? Guid.NewGuid().ToString("N") : entry.FolderId,
            ThreadId = entry.ThreadId.StartsWith("folder_", StringComparison.OrdinalIgnoreCase) ? null : entry.ThreadId,
            Name = entry.Name,
            CreatedAt = NormalizeIsoTimestamp(entry.UploadedAt),
            ParentFolderId = NormalizeFolderParentId(entry.ParentFolderId),
            FolderColor = string.IsNullOrWhiteSpace(entry.FolderColor) ? "#C4C7C5" : entry.FolderColor
        };
    }

    private static void Validate(MetadataEntry metadata)
    {
        if (metadata.SchemaVersion != SchemaVersion)
        {
            throw new InvalidOperationException("schemaVersion invÃ¡lido em metadata.json.");
        }

        switch (metadata)
        {
            case FileMetadata file:
                Require(file.EntryType == "file", "entryType de arquivo invÃ¡lido.");
                Require(!string.IsNullOrWhiteSpace(file.FileId), "fileId ausente.");
                Require(!string.IsNullOrWhiteSpace(file.ThreadId), "threadId ausente.");
                Require(!string.IsNullOrWhiteSpace(file.Name), "name ausente.");
                Require(file.SizeBytes >= 0, "sizeBytes invÃ¡lido.");
                file.Chunks = file.Chunks?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList() ?? [];
                Require(file.Chunks.Count > 0, "chunks do arquivo ausentes.");
                if (file.Encryption != null)
                {
                    Require(file.Encryption.Version == EncryptionMetadata.CurrentVersion, "versão de criptografia inválida.");
                    Require(EncryptionModes.IsSupported(file.Encryption.Mode), "modo de criptografia inválido.");
                    try
                    {
                        Require(EncryptionMetadataCodec.DecodeSalt(file.Encryption.Salt).Length >= 16, "salt de criptografia inválido.");
                    }
                    catch (FormatException)
                    {
                        throw new InvalidOperationException("salt de criptografia inválido.");
                    }
                }
                if (file.Preview != null)
                {
                    var hasThumbnail = !string.IsNullOrWhiteSpace(file.Preview.ThumbnailMessageId);
                    if (!hasThumbnail)
                    {
                        file.Preview.ThumbnailMessageId = null;
                    }

                    if (file.Preview.Video480p != null)
                    {
                        if (!file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                            file.Preview.Video480p.SizeBytes <= 0 ||
                            file.Preview.Video480p.Chunks == null ||
                            file.Preview.Video480p.Chunks.Count == 0)
                        {
                            file.Preview.Video480p = null;
                        }
                        else
                        {
                            file.Preview.Video480p.Chunks = file.Preview.Video480p.Chunks
                                .Where(id => !string.IsNullOrWhiteSpace(id))
                                .ToList();
                        }
                    }

                    if (!hasThumbnail && file.Preview.Video480p == null)
                    {
                        file.Preview = null;
                    }
                }
                break;
            case FolderMetadata folder:
                Require(folder.EntryType == "folder", "entryType de pasta invÃ¡lido.");
                Require(!string.IsNullOrWhiteSpace(folder.FolderId), "folderId ausente.");
                Require(!string.IsNullOrWhiteSpace(folder.Name), "name ausente.");
                break;
            default:
                throw new InvalidOperationException("metadata.json desconhecido.");
        }
    }

    private static void ResolvePaths(List<AppEntry> entries)
    {
        var folders = entries
            .Where(entry => entry.Type == "folder" && !string.IsNullOrWhiteSpace(entry.FolderId))
            .ToDictionary(entry => entry.FolderId, StringComparer.OrdinalIgnoreCase);
        var resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string FolderFullPath(AppEntry folder)
        {
            ResolveFolderPath(folder);
            return PathRules.Normalize(folder.Path == "/" ? "/" + folder.Name : folder.Path + "/" + folder.Name);
        }

        void ResolveFolderPath(AppEntry folder)
        {
            if (string.IsNullOrWhiteSpace(folder.FolderId) || !resolving.Add(folder.FolderId))
            {
                folder.Path = "/";
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(folder.ParentFolderId) ||
                    folder.ParentFolderId.Equals("root", StringComparison.OrdinalIgnoreCase) ||
                    !folders.TryGetValue(folder.ParentFolderId, out var parent))
                {
                    folder.Path = "/";
                    return;
                }

                folder.Path = FolderFullPath(parent);
            }
            finally
            {
                resolving.Remove(folder.FolderId);
            }
        }

        foreach (var folder in folders.Values)
        {
            ResolveFolderPath(folder);
        }

        foreach (var file in entries.Where(entry => entry.Type != "folder"))
        {
            file.Path = !string.IsNullOrWhiteSpace(file.ParentFolderId) &&
                        folders.TryGetValue(file.ParentFolderId, out var parent)
                ? FolderFullPath(parent)
                : "/";
        }
    }

    private static bool IsMetadataAttachment(DiscordAttachmentDto attachment)
    {
        return attachment.FileName.Equals(FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeParentId(string? parentFolderId)
    {
        return string.IsNullOrWhiteSpace(parentFolderId) ? null : parentFolderId;
    }

    private static string NormalizeFolderParentId(string? parentFolderId)
    {
        return string.IsNullOrWhiteSpace(parentFolderId) ? "root" : parentFolderId;
    }

    private static string NormalizeIsoTimestamp(string value)
    {
        if (DateTimeOffset.TryParse(value, out var parsed))
        {
            return parsed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }

        return DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public sealed record MetadataWritePayload(string Content, FileInfo? Attachment);
