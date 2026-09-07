using System.Text.Json.Serialization;

namespace Organessonium.Models;

public abstract class MetadataEntry
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("entryType")]
    public string EntryType { get; set; } = "";
}

public sealed class FileMetadata : MetadataEntry
{
    public FileMetadata()
    {
        EntryType = "file";
    }

    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = "";

    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
[JsonPropertyName("uploadedAt")]
    public string UploadedAt { get; set; } = "";

    [JsonPropertyName("batchId")]
    public string BatchId { get; set; } = "";

    [JsonPropertyName("parentFolderId")]
    public string? ParentFolderId { get; set; }

    [JsonPropertyName("chunks")]
    public List<string> Chunks { get; set; } = [];

    [JsonPropertyName("preview")]
    public PreviewMetadata? Preview { get; set; }
}

public sealed class FolderMetadata : MetadataEntry
{
    public FolderMetadata()
    {
        EntryType = "folder";
    }

    [JsonPropertyName("folderId")]
    public string FolderId { get; set; } = "";

    [JsonPropertyName("threadId")]
    public string? ThreadId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("parentFolderId")]
    public string? ParentFolderId { get; set; }

    [JsonPropertyName("folderColor")]
    public string FolderColor { get; set; } = "#C4C7C5";
}

public sealed class PreviewMetadata
{
    [JsonPropertyName("thumbnailMessageId")]
    public string? ThumbnailMessageId { get; set; }

    [JsonPropertyName("video480p")]
    public VideoPreviewMetadata? Video480p { get; set; }
}

public sealed class VideoPreviewMetadata
{

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("chunks")]
    public List<string> Chunks { get; set; } = [];
}

public sealed record MetadataEnvelope(string MessageId, MetadataEntry Entry);
