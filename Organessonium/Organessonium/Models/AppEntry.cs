using System.Text.Json.Serialization;

namespace Organessonium.Models;

public sealed class AppEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = "";

    [JsonPropertyName("uploadedAt")]
    public string UploadedAt { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("metadataMessageId")]
    public string MetadataMessageId { get; set; } = "";

    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = "";

    [JsonPropertyName("folderId")]
    public string FolderId { get; set; } = "";

    [JsonPropertyName("parentFolderId")]
    public string? ParentFolderId { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "/";

    [JsonPropertyName("folderColor")]
    public string FolderColor { get; set; } = "";

    [JsonPropertyName("deviceType")]
    public string? DeviceType { get; set; }

    [JsonPropertyName("favorite")]
    public bool Favorite { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "file";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "";
[JsonIgnore]
    public List<string> ChunkMessageIds { get; set; } = [];

    [JsonIgnore]
    public List<ChunkReference>? ChunkReferences { get; set; }

    [JsonIgnore]
    public List<string> VideoPreviewChunkMessageIds { get; set; } = [];

    [JsonIgnore]
    public List<ChunkReference>? VideoPreviewChunkReferences { get; set; }

    [JsonIgnore]
    public int LogicalChunkCount => ChunkReferences?.Count ?? ChunkMessageIds.Count;

    [JsonIgnore]
    public int ChunkCount { get; set; }

    [JsonPropertyName("batchId")]
    public string BatchId { get; set; } = "";

    [JsonPropertyName("previewMessageId")]
    public string PreviewMessageId { get; set; } = "";

    [JsonPropertyName("videoPreviewMessageId")]
    public string VideoPreviewMessageId { get; set; } = "";

    [JsonPropertyName("videoPreviewSizeBytes")]
    public long VideoPreviewSizeBytes { get; set; }

    [JsonPropertyName("encryption")]
    public EncryptionMetadata? Encryption { get; set; }

    [JsonPropertyName("trash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrashMetadata? Trash { get; set; }

    [JsonPropertyName("sync")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncMetadata? Sync { get; set; }

    [JsonPropertyName("syncDevice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncDeviceMetadata? SyncDevice { get; set; }

    public static AppEntry File(string name, string threadId, string uploadedAt, long sizeBytes, string? path)
    {
        return new AppEntry
        {
            Name = name,
            ThreadId = threadId,
            UploadedAt = uploadedAt,
            SizeBytes = sizeBytes,
            Path = PathRules.Normalize(path),
            Type = "file",
            MimeType = GuessMimeType(name),
            FileId = Guid.NewGuid().ToString("N")
        };
    }

    public static AppEntry Folder(string name, string? parentPath)
    {
        return new AppEntry
        {
            Name = name,
            ThreadId = "folder_" + Guid.NewGuid().ToString("N")[..16],
            UploadedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            SizeBytes = 0,
            Path = PathRules.Normalize(parentPath),
        FolderColor = "#C4C7C5",
            Type = "folder",
            FolderId = Guid.NewGuid().ToString("N")
        };
    }

    public void Normalize()
    {
        Path = PathRules.Normalize(Path);
        Type = string.IsNullOrWhiteSpace(Type) ? "file" : Type;
        MetadataMessageId ??= "";
        FileId ??= "";
        FolderId ??= "";
        ParentFolderId = string.IsNullOrWhiteSpace(ParentFolderId) ? null : ParentFolderId;
        DeviceType = DeviceType?.Trim().ToLowerInvariant() switch
        {
            "phone" => "phone",
            "pc" => "pc",
            _ => null
        };
        MimeType ??= "";
        ChunkMessageIds ??= [];
        VideoPreviewChunkMessageIds ??= [];
        if (ChunkReferences != null)
        {
            ChunkReferences = ChunkReference.Validate(ChunkReferences, ChunkMessageIds);
            ChunkCount = ChunkReferences.Count;
        }
        if (VideoPreviewChunkReferences != null)
            VideoPreviewChunkReferences = ChunkReference.Validate(VideoPreviewChunkReferences, VideoPreviewChunkMessageIds);
        BatchId ??= "";
        PreviewMessageId ??= "";
        VideoPreviewMessageId ??= "";
        VideoPreviewSizeBytes = Math.Max(0, VideoPreviewSizeBytes);
        if (Encryption != null)
        {
            Encryption.Mode = EncryptionModes.Normalize(Encryption.Mode);
            Encryption.Salt ??= "";
        }
        if (Type == "file" && string.IsNullOrWhiteSpace(FileId))
        {
            FileId = Guid.NewGuid().ToString("N");
        }

        if (Type == "folder" && string.IsNullOrWhiteSpace(FolderId))
        {
            FolderId = Guid.NewGuid().ToString("N");
        }

        if (Type == "folder" && string.IsNullOrWhiteSpace(FolderColor))
        {
            FolderColor = "#C4C7C5";
        }
    }

    private static string GuessMimeType(string name)
    {
        return System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "webp" => "image/webp",
            "wbmp" => "image/vnd.wap.wbmp",
            "mp4" or "m4v" => "video/mp4",
            "mov" => "video/quicktime",
            "webm" => "video/webm",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "3gp" => "video/3gpp",
            "pdf" => "application/pdf",
            "md" or "markdown" => "text/markdown",
            "txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}

public static class PathRules
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        if (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    public static string ChildPath(AppEntry folder)
    {
        var parent = Normalize(folder.Path);
        var child = parent == "/" ? "/" + folder.Name : parent + "/" + folder.Name;
        return Normalize(child);
    }

    public static string CleanFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => char.IsControl(ch) || invalid.Contains(ch) ? '_' : ch).ToArray();
        var cleaned = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "arquivo" : cleaned;
    }
}
