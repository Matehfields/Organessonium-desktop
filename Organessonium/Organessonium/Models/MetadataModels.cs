using System.Text.Json.Serialization;

namespace Organessonium.Models;

public abstract class MetadataEntry
{
    [JsonPropertyName("libraryOperationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LibraryOperationId { get; set; }

    [JsonPropertyName("modifiedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedAt { get; set; }
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("entryType")]
    public string EntryType { get; set; } = "";

    [JsonPropertyName("trash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrashMetadata? Trash { get; set; }

    [JsonPropertyName("sync")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncMetadata? Sync { get; set; }

    [JsonPropertyName("syncDevice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncDeviceMetadata? SyncDevice { get; set; }
}

public sealed class TrashMetadata
{
    [JsonPropertyName("originalParentFolderId")]
    public string OriginalParentFolderId { get; set; } = "root";

    [JsonPropertyName("deletedAt")]
    public string DeletedAt { get; set; } = "";
}

public sealed class FileMetadata : MetadataEntry
{
    [JsonPropertyName("checksums")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PayloadChecksums? Checksums { get; set; }
    [JsonPropertyName("storage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StorageLayout? Storage { get; set; }
    [JsonPropertyName("versionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VersionId { get; set; }

    [JsonPropertyName("versionUploadedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VersionUploadedAt { get; set; }

    [JsonPropertyName("versions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FileVersion>? Versions { get; set; }
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Chunks { get; set; } = [];

    [JsonPropertyName("chunkRefs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChunkReference>? ChunkRefs { get; set; }

    [JsonPropertyName("preview")]
    public PreviewMetadata? Preview { get; set; }

    [JsonPropertyName("encryption")]
    public EncryptionMetadata? Encryption { get; set; }

    [JsonPropertyName("fav")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Favorite { get; set; }
}

public sealed class FileVersion
{
    [JsonPropertyName("versionId")]
    public string VersionId { get; set; } = "";

    [JsonPropertyName("archivedAt")]
    public string ArchivedAt { get; set; } = "";

    [JsonPropertyName("payload")]
    public FileMetadata Payload { get; set; } = new();
}

public sealed class FileReplacement
{
    [JsonPropertyName("metadataMessageId")]
    public string MetadataMessageId { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "/";

    [JsonPropertyName("metadata")]
    public FileMetadata Metadata { get; set; } = new();
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
    [JsonPropertyName("thumbnailRef")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttachmentReference? ThumbnailRef { get; set; }
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Chunks { get; set; } = [];

    [JsonPropertyName("chunkRefs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChunkReference>? ChunkRefs { get; set; }
}

public sealed record MetadataEnvelope(string MessageId, MetadataEntry Entry);
