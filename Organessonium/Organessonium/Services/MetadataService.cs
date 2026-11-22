using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

public sealed class MetadataService
{
    public const int SchemaVersion = 2;
    public const string FileName = "metadata.json";
    public const int InlineContentLimit = DiscordLimits.MessageContentCharacters;
    public const long MaxMetadataBytes = DiscordLimits.AttachmentBytes;
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonOptions.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IMetadataCodec _metadataCodec;
    private readonly Func<DiscordAttachmentDto, CancellationToken, Task<byte[]>> _downloadAttachment;

    public MetadataService(IMetadataCodec? codec = null,
        Func<DiscordAttachmentDto, CancellationToken, Task<byte[]>>? downloadAttachment = null)
    {
        _metadataCodec = codec ?? MetadataCodec.Standard();
        _downloadAttachment = downloadAttachment ?? DownloadAttachmentAsync;
    }

    public string EncodeJson(string json) => _metadataCodec.Encode(json);
    public string DecodeJson(string json) => _metadataCodec.Decode(json);
    public string EncodePayloadReference(string content) => EncodeJson(JsonSerializer.Serialize(new
        { schemaVersion = 1, entryType = "payloadReference", content }, JsonOptions.Web));

    public string DecodePayloadReference(string stored)
    {
        if (!stored.TrimStart().StartsWith('{')) return stored;
        using var document = JsonDocument.Parse(DecodeJson(stored));
        var root = document.RootElement;
        if (root.TryGetProperty("entryType", out var type) && type.GetString() == "payloadReference" &&
            root.TryGetProperty("schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number && version.GetInt32() == 1 &&
            root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString()!;
        if (_metadataCodec.IsEncrypted(stored)) throw new MetadataCodecException(MetadataError.InvalidFormat);
        return stored;
    }

    public async Task<MetadataWritePayload> CreateWritePayloadAsync(AppEntry entry, CancellationToken cancellationToken = default)
    {
        var metadata = CreateMetadata(entry);
        var compactJson = EncodeJson(SerializeMetadata(metadata, CompactJsonOptions));
        if (compactJson.Length <= InlineContentLimit)
        {
            return new MetadataWritePayload(compactJson, null);
        }

        return new MetadataWritePayload("", await CreateEncodedFileAsync(compactJson, cancellationToken));
    }

    public async Task<string> SendEntryAsync(
        DiscordApiClient discord,
        string channelId,
        AppEntry entry,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool replayUnknownResult = true)
    {
        entry.Normalize();
        var payload = await CreateWritePayloadAsync(entry, cancellationToken);
        try
        {
            var message = payload.Attachment == null
                ? await discord.CreateMessageAsync(
                    channelId,
                    payload.Content,
                    cancellationToken,
                    onRateLimitWait,
                    replayUnknownResult: replayUnknownResult)
                : await SendMetadataFileAsync(
                    discord,
                    channelId,
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
        string channelId,
        AppEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(entry.MetadataMessageId))
        {
            await discord.DeleteMessageAsync(channelId, entry.MetadataMessageId, cancellationToken);
        }
    }

    public async Task<string> UpsertEntryAsync(
        DiscordApiClient discord,
        string channelId,
        AppEntry entry,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        entry.Normalize();
        var payload = await CreateWritePayloadAsync(entry, cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.MetadataMessageId) && payload.Attachment == null)
            {
                var updated = await discord.UpdateMessageAsync(
                    channelId,
                    entry.MetadataMessageId,
                    payload.Content,
                    cancellationToken,
                    onRateLimitWait,
                    clearAttachments: true);
                entry.MetadataMessageId = updated.Id;
                return updated.Id;
            }

            var message = payload.Attachment == null
                ? await discord.CreateMessageAsync(channelId, payload.Content, cancellationToken, onRateLimitWait)
                : string.IsNullOrWhiteSpace(entry.MetadataMessageId)
                    ? await SendMetadataFileAsync(discord, channelId, payload.Attachment, cancellationToken, onRateLimitWait)
                    : await discord.ReplaceFileMessageAsync(channelId, entry.MetadataMessageId, "metadata", payload.Attachment, cancellationToken);
            entry.MetadataMessageId = message.Id;
            return message.Id;
        }
        finally
        {
            try { payload.Attachment?.Directory?.Delete(true); } catch { }
        }
    }

    public string SerializeFolderIndex(FolderIndexMapping mapping)
    {
        var metadata = new FolderIndexMetadata
        {
            SchemaVersion = SchemaVersion,
            FolderId = mapping.FolderId,
            ThreadId = mapping.ThreadId,
            SerialMessageId = string.IsNullOrWhiteSpace(mapping.SerialMessageId) ? null : mapping.SerialMessageId,
            SerialSha256 = string.IsNullOrWhiteSpace(mapping.SerialSha256) ? null : mapping.SerialSha256
        };
        Validate(metadata);
        return EncodeJson(SerializeMetadata(metadata, CompactJsonOptions));
    }

    public string SerializeFolderIndexHeader(string folderId, string parentFolderId, string name)
    {
        var metadata = new FolderIndexHeaderMetadata
        {
            SchemaVersion = SchemaVersion,
            FolderId = folderId,
            ParentFolderId = string.IsNullOrWhiteSpace(parentFolderId) ? "root" : parentFolderId,
            Name = name
        };
        Validate(metadata);
        return EncodeJson(SerializeMetadata(metadata, CompactJsonOptions));
    }

    public async Task<IReadOnlyList<FolderIndexMapping>> ReadFolderIndexMappingsInParallelAsync(
        IReadOnlyList<DiscordMessageDto> messages,
        int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        var envelopes = await ReadEnvelopesInParallelAsync(messages, maxConcurrency, cancellationToken);
        return envelopes
            .Where(envelope => envelope.Entry is FolderIndexMetadata)
            .Select(envelope =>
            {
                var index = (FolderIndexMetadata)envelope.Entry;
                return new FolderIndexMapping(
                    index.FolderId,
                    index.ThreadId,
                    envelope.MessageId,
                    index.SerialMessageId ?? "",
                    index.SerialSha256 ?? "");
            })
            .ToList();
    }

    public List<AppEntry> ToFolderEntries(
        IReadOnlyList<MetadataEnvelope> envelopes,
        string folderId,
        string folderPath,
        IReadOnlyDictionary<string, FolderIndexMapping> mappings)
    {
        var normalizedFolderId = string.IsNullOrWhiteSpace(folderId) ? "root" : folderId;
        var result = new List<AppEntry>();
        foreach (var envelope in envelopes)
        {
            var entry = ToAppEntry(envelope);
            if (entry == null)
            {
                continue;
            }

            // The folder thread is authoritative for direct membership. Older
            // metadata may not have a parentFolderId, so accept that legacy
            // shape and normalize it to the folder being read.
            if (!string.IsNullOrWhiteSpace(entry.ParentFolderId) &&
                !string.Equals(entry.ParentFolderId, normalizedFolderId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entry.Path = PathRules.Normalize(folderPath);
            entry.ParentFolderId = normalizedFolderId;
            if (entry.Type == "folder" && mappings.TryGetValue(entry.FolderId, out var mapping))
            {
                entry.ThreadId = mapping.ThreadId;
            }

            entry.Normalize();
            result.Add(entry);
        }

        return result;
    }

    public async Task<FileInfo> CreateMetadataFileAsync(AppEntry entry, CancellationToken cancellationToken = default)
    {
        var metadata = CreateMetadata(entry);
        return await CreateEncodedFileAsync(EncodeJson(SerializeMetadata(metadata, CompactJsonOptions)), cancellationToken);
    }

    private static async Task<FileInfo> CreateEncodedFileAsync(
        string json,
        CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxMetadataBytes)
        {
            throw new MetadataCodecException(MetadataError.TooLarge);
        }

        var dir = Directory.CreateTempSubdirectory("organessonium_metadata_");
        var file = new FileInfo(Path.Combine(dir.FullName, FileName));
        try
        {
            await File.WriteAllTextAsync(file.FullName, json, MetadataCryptoParameters.Utf8, cancellationToken);
            file.Refresh();
            return file;
        }
        catch { dir.Delete(true); throw; }
    }

    public async Task<MetadataEnvelope?> ReadEnvelopeAsync(
        DiscordMessageDto message,
        CancellationToken cancellationToken = default)
    {
        var payload = await ReadJsonPayloadAsync(message, cancellationToken);
        if (payload == null) return null;
        try
        {
            using var document = JsonDocument.Parse(payload.Value.Json);
            var metadata = ParseMetadata(document.RootElement);
            if (metadata == null)
            {
                if (payload.Value.Encrypted)
                {
                    if (!document.RootElement.TryGetProperty("entryType", out var type) || type.ValueKind != JsonValueKind.String)
                        throw new MetadataCodecException(MetadataError.MissingField);
                    if (type.GetString() is "file" or "folder" or "folderIndex" or "folderIndexHeader")
                    {
                        if (!document.RootElement.TryGetProperty("schemaVersion", out _)) throw new MetadataCodecException(MetadataError.MissingField);
                        throw new MetadataCodecException(MetadataError.UnsupportedVersion);
                    }
                    if (type.GetString() is not ("syncRevision" or "syncApplied" or "folderSerial" or "folderSerialData" or "payloadReference" or
                        "storageThread" or "storageControl" or "storageReference" or "storageCleanup" or "storageCleanupApplied" or
                        "storageBatchPlan" or "storageBatchReceipt" or "storageMutation"))
                        throw new MetadataCodecException(MetadataError.UnsupportedVersion);
                }
                return null;
            }

            Validate(metadata);
            return new MetadataEnvelope(message.Id, metadata);
        }
        catch (MetadataCodecException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            if (payload.Value.Encrypted) throw new MetadataCodecException(MetadataError.MissingField);
            return null;
        }
    }

    public async Task<JsonDocument?> ReadJsonAsync(DiscordMessageDto message, CancellationToken cancellationToken = default)
    {
        var payload = await ReadJsonPayloadAsync(message, cancellationToken);
        return payload == null ? null : JsonDocument.Parse(payload.Value.Json);
    }

    private async Task<(string Json, bool Encrypted)?> ReadJsonPayloadAsync(DiscordMessageDto message, CancellationToken token)
    {
        var content = message.Content?.Trim() ?? "";
        if (content.StartsWith('{'))
        {
            try
            {
                using var outer = JsonDocument.Parse(content);
                var encrypted = _metadataCodec.IsEncrypted(content);
                var decoded = DecodeJson(content);
                if (encrypted || outer.RootElement.TryGetProperty("schemaVersion", out _) || outer.RootElement.TryGetProperty("schema", out _))
                    return (decoded, encrypted);
            }
            catch (JsonException)
            {
                if (content.Contains(MetadataCryptoParameters.Format, StringComparison.Ordinal) || content.Contains("\"encrypted\"", StringComparison.Ordinal))
                    throw new MetadataCodecException(MetadataError.InvalidFormat);
            }
        }
        var attachment = message.Attachments.FirstOrDefault(a => IsMetadataAttachment(a) || a.FileName == "sync-revision.json");
        if (attachment == null) return null;
        if (attachment.Size > MaxMetadataBytes) throw new MetadataCodecException(MetadataError.TooLarge);
        var bytes = await _downloadAttachment(attachment, token);
        if (bytes.Length > MaxMetadataBytes) throw new MetadataCodecException(MetadataError.TooLarge);
        string stored;
        try { stored = MetadataCryptoParameters.Utf8.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { throw new MetadataCodecException(MetadataError.InvalidFormat); }
        return (DecodeJson(stored), _metadataCodec.IsEncrypted(stored));
    }

    private static async Task<byte[]> DownloadAttachmentAsync(DiscordAttachmentDto attachment, CancellationToken token)
    {
        using var http = NetworkConfig.Current.CreateAttachmentClient();
        using var response = await http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > MaxMetadataBytes) throw new MetadataCodecException(MetadataError.TooLarge);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
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

        var results = new MetadataEnvelope?[messages.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, messages.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, maxConcurrency)
            },
            async (index, token) =>
            {
                results[index] = await ReadEnvelopeAsync(messages[index], token);
            });
        return results
            .Where(envelope => envelope != null)
            .Cast<MetadataEnvelope>()
            .ToList();
    }

    public AppEntry? ToAppEntry(MetadataEnvelope envelope)
    {
        AppEntry entry;
        switch (envelope.Entry)
        {
            case FileMetadata file:
                entry = AppEntry.File(file.Name, file.ThreadId, file.UploadedAt, file.SizeBytes, "/");
                entry.Checksums = file.Checksums;
                entry.Storage = file.Storage;
                entry.ThumbnailRef = file.Preview?.ThumbnailRef;
                entry.FileId = file.FileId;
                entry.ParentFolderId = NormalizeParentId(file.ParentFolderId);
                entry.MimeType = file.MimeType;
                entry.BatchId = file.BatchId;
                entry.ChunkReferences = file.ChunkRefs == null ? null : ChunkReference.Validate(file.ChunkRefs, file.Chunks);
                entry.ChunkMessageIds = entry.ChunkReferences == null
                    ? (file.Chunks ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToList() : [];
                entry.ChunkCount = entry.LogicalChunkCount;
                entry.PreviewMessageId = file.Preview?.ThumbnailRef?.MessageId ?? file.Preview?.ThumbnailMessageId ?? "";
                entry.VideoPreviewChunkMessageIds = file.Preview?.Video480p?.Chunks?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList() ?? [];
                entry.VideoPreviewChunkReferences = file.Preview?.Video480p?.ChunkRefs is { } videoReferences
                    ? ChunkReference.Validate(videoReferences, file.Preview.Video480p.Chunks) : null;
                if (entry.VideoPreviewChunkReferences != null) entry.VideoPreviewChunkMessageIds = [];
                entry.VideoPreviewMessageId = entry.VideoPreviewChunkReferences?.FirstOrDefault()?.MessageId ??
                    entry.VideoPreviewChunkMessageIds.FirstOrDefault() ?? "";
                entry.VideoPreviewSizeBytes = Math.Max(0, file.Preview?.Video480p?.SizeBytes ?? 0);
                entry.Encryption = file.Encryption?.Clone();
                entry.Favorite = file.Favorite;
                entry.VersionId = file.VersionId ?? "";
                entry.VersionUploadedAt = file.VersionUploadedAt ?? file.UploadedAt;
                entry.Versions = file.Versions ?? [];
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
                entry.DeviceType = folder.DeviceType;
                entry.Favorite = folder.Favorite;
                break;
            default:
                return null;
        }

        entry.MetadataMessageId = envelope.MessageId;
        entry.ModifiedAt = envelope.Entry.ModifiedAt ?? entry.UploadedAt;
        entry.Trash = envelope.Entry.Trash;
        entry.Sync = envelope.Entry.Sync;
        entry.SyncDevice = envelope.Entry.SyncDevice;
        entry.Normalize();
        return entry;
    }

    public static FileMetadata CreateFileMetadata(AppEntry entry)
    {
        return new FileMetadata
        {
            SchemaVersion = SchemaVersion,
            Storage = entry.Storage,
            FileId = string.IsNullOrWhiteSpace(entry.FileId) ? Guid.NewGuid().ToString("N") : entry.FileId,
            ThreadId = entry.ThreadId,
            Name = entry.Name,
            MimeType = entry.MimeType,
            SizeBytes = entry.SizeBytes,
            UploadedAt = NormalizeIsoTimestamp(entry.UploadedAt),
            ModifiedAt = string.IsNullOrWhiteSpace(entry.ModifiedAt) ? null : NormalizeIsoTimestamp(entry.ModifiedAt),
            VersionId = string.IsNullOrWhiteSpace(entry.VersionId) ? null : entry.VersionId,
            VersionUploadedAt = string.IsNullOrWhiteSpace(entry.VersionUploadedAt) ? null : NormalizeIsoTimestamp(entry.VersionUploadedAt),
            Versions = entry.Versions.Count == 0 ? null : entry.Versions,
            BatchId = entry.BatchId,
            ParentFolderId = NormalizeParentId(entry.ParentFolderId),
            Chunks = entry.ChunkReferences == null ? entry.ChunkMessageIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() : null,
            ChunkRefs = entry.ChunkReferences?.ToList(),
            Checksums = entry.Checksums,
            Preview = CreatePreviewMetadata(entry),
            Encryption = entry.Encryption?.Clone(),
            Trash = entry.Trash,
            Sync = entry.Sync,
            Favorite = entry.Favorite
        };
    }

    private static PreviewMetadata? CreatePreviewMetadata(AppEntry entry)
    {
        var thumbnailMessageId = string.IsNullOrWhiteSpace(entry.PreviewMessageId) ? null : entry.PreviewMessageId;
        var videoChunks = entry.VideoPreviewChunkMessageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (entry.VideoPreviewChunkReferences == null && videoChunks.Count == 0 && !string.IsNullOrWhiteSpace(entry.VideoPreviewMessageId))
        {
            videoChunks.Add(entry.VideoPreviewMessageId);
        }

        VideoPreviewMetadata? video480p = null;
        if ((entry.VideoPreviewChunkReferences != null || videoChunks.Count > 0) && entry.VideoPreviewSizeBytes > 0)
        {
            video480p = new VideoPreviewMetadata
            {
                SizeBytes = entry.VideoPreviewSizeBytes,
                Chunks = entry.VideoPreviewChunkReferences == null ? videoChunks : null,
                ChunkRefs = entry.VideoPreviewChunkReferences?.ToList()
            };
        }

        if (thumbnailMessageId == null && entry.ThumbnailRef == null && video480p == null)
        {
            return null;
        }

        return new PreviewMetadata
        {
            ThumbnailRef = entry.ThumbnailRef,
            ThumbnailMessageId = entry.ThumbnailRef == null ? thumbnailMessageId : null,
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
        ChunkReference.ValidateJsonPresence(root);
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
            "folderIndex" => root.Deserialize<FolderIndexMetadata>(JsonOptions.Web),
            "folderIndexHeader" => root.Deserialize<FolderIndexHeaderMetadata>(JsonOptions.Web),
            _ => null
        };
    }

    private static FolderMetadata CreateFolderMetadata(AppEntry entry)
    {
        return new FolderMetadata
        {
            SchemaVersion = SchemaVersion,
            FolderId = string.IsNullOrWhiteSpace(entry.FolderId) ? Guid.NewGuid().ToString("N") : entry.FolderId,
            // The authoritative folderId -> threadId relationship lives in names.
            ThreadId = null,
            Name = entry.Name,
            CreatedAt = NormalizeIsoTimestamp(entry.UploadedAt),
            ModifiedAt = string.IsNullOrWhiteSpace(entry.ModifiedAt) ? null : NormalizeIsoTimestamp(entry.ModifiedAt),
            ParentFolderId = NormalizeFolderParentId(entry.ParentFolderId),
            FolderColor = string.IsNullOrWhiteSpace(entry.FolderColor) ? "#C4C7C5" : entry.FolderColor,
            DeviceType = entry.DeviceType,
            Trash = entry.Trash,
            Sync = entry.Sync,
            SyncDevice = entry.SyncDevice,
            Favorite = entry.Favorite
        };
    }

    internal static void Validate(MetadataEntry metadata)
    {
        if (metadata.SchemaVersion != SchemaVersion)
        {
            throw new InvalidOperationException("schemaVersion invÃ¡lido em metadata.json.");
        }

        switch (metadata)
        {
            case FileMetadata file:
                file.Checksums?.Validate(file.SizeBytes, file.ChunkRefs?.Count ?? file.Chunks?.Count ?? 0);
                file.Storage?.Validate();
                file.Preview?.ThumbnailRef?.Validate();
                if (file.Storage?.Shared == true && file.Preview?.Video480p != null && file.Preview.Video480p.ChunkRefs == null)
                    throw new InvalidDataException("A prévia compartilhada deve usar chunkRefs completos.");
                if (file.Storage?.Shared == true && (file.ChunkRefs == null || file.Encryption == null ||
                    (!string.IsNullOrEmpty(file.Preview?.ThumbnailMessageId) && file.Preview.ThumbnailRef == null)))
                    throw new InvalidDataException("O arquivo compartilhado exige referências exatas e criptografia.");
                if (file.Versions != null)
                {
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var version in file.Versions)
                    {
                        Require(!string.IsNullOrWhiteSpace(version.VersionId) && ids.Add(version.VersionId), "Identificador de versão ausente ou duplicado.");
                        Require(version.Payload != null && version.Payload.Versions == null, "Histórico de versões aninhado inválido.");
                        Require(version.Payload?.VersionId == version.VersionId, "Identificador do payload histórico inválido.");
                        Validate(version.Payload!);
                        Require(version.Payload!.FileId == file.FileId, "A versão pertence a outro arquivo.");
                    }
                }
                Require(file.EntryType == "file", "entryType de arquivo invÃ¡lido.");
                Require(!string.IsNullOrWhiteSpace(file.FileId), "fileId ausente.");
                Require(!string.IsNullOrWhiteSpace(file.ThreadId), "threadId ausente.");
                Require(!string.IsNullOrWhiteSpace(file.Name), "name ausente.");
                Require(file.SizeBytes >= 0, "sizeBytes invÃ¡lido.");
                if (file.ChunkRefs != null)
                    file.ChunkRefs = ChunkReference.Validate(file.ChunkRefs, file.Chunks);
                else
                {
                    file.Chunks = file.Chunks?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? [];
                    Require(file.Chunks.Count > 0, "chunks do arquivo ausentes.");
                }
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
                    var hasThumbnail = file.Preview.ThumbnailRef != null || !string.IsNullOrWhiteSpace(file.Preview.ThumbnailMessageId);
                    if (file.Preview.ThumbnailRef != null || !hasThumbnail)
                    {
                        file.Preview.ThumbnailMessageId = null;
                    }

                    if (file.Preview.Video480p != null)
                    {
                        if (file.Preview.Video480p.ChunkRefs != null)
                        {
                            Require(file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) &&
                                file.Preview.Video480p.SizeBytes > 0, "Prévia 480p com referências inválidas.");
                            file.Preview.Video480p.ChunkRefs = ChunkReference.Validate(
                                file.Preview.Video480p.ChunkRefs, file.Preview.Video480p.Chunks);
                        }
                        else if (!file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
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
            case FolderIndexMetadata index:
                Require(index.EntryType == "folderIndex", "entryType de índice de pasta inválido.");
                Require(!string.IsNullOrWhiteSpace(index.FolderId), "folderId ausente no índice.");
                Require(!string.IsNullOrWhiteSpace(index.ThreadId), "threadId ausente no índice.");
                if (!string.IsNullOrWhiteSpace(index.SerialSha256))
                {
                    Require(index.SerialSha256.All(Uri.IsHexDigit), "serialSha256 inválido.");
                }
                break;
            case FolderIndexHeaderMetadata header:
                Require(header.EntryType == "folderIndexHeader", "entryType de cabeçalho de pasta inválido.");
                Require(!string.IsNullOrWhiteSpace(header.FolderId), "folderId ausente no cabeçalho.");
                Require(!string.IsNullOrWhiteSpace(header.Name), "name ausente no cabeçalho.");
                break;
            default:
                throw new InvalidOperationException("metadata.json desconhecido.");
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
