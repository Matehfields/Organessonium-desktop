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

    [JsonPropertyName("encryption")]
    public EncryptionMetadata? Encryption { get; set; }

    [JsonPropertyName("fav")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Favorite { get; set; }
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

    [JsonPropertyName("deviceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceType { get; set; }

    [JsonPropertyName("fav")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Favorite { get; set; }
}

// Stored in the names text channel. It is intentionally limited to locating a
// folder index thread and its latest serial, never to listing every file.
public sealed class FolderIndexMetadata : MetadataEntry
{
    public FolderIndexMetadata()
    {
        EntryType = "folderIndex";
    }

    [JsonPropertyName("folderId")]
    public string FolderId { get; set; } = "";

    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = "";

    [JsonPropertyName("serialMessageId")]
    public string? SerialMessageId { get; set; }

    [JsonPropertyName("serialSha256")]
    public string? SerialSha256 { get; set; }
}

// The first message of an index thread identifies the folder for human
// inspection and validation. Child metadata messages stay in the same thread.
public sealed class FolderIndexHeaderMetadata : MetadataEntry
{
    public FolderIndexHeaderMetadata()
    {
        EntryType = "folderIndexHeader";
    }

    [JsonPropertyName("folderId")]
    public string FolderId { get; set; } = "";

    [JsonPropertyName("parentFolderId")]
    public string ParentFolderId { get; set; } = "root";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed record FolderIndexMapping(
    string FolderId,
    string ThreadId,
    string NamesMessageId,
    string SerialMessageId = "",
    string SerialSha256 = "");

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
