using System.Text.Json.Serialization;

namespace Organessonium.Models;

internal sealed class StorageThreadRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("entryType")]
    public string EntryType { get; set; } = "storageThread";
    [JsonPropertyName("layoutVersion")]
    public int LayoutVersion { get; set; } = 2;
    [JsonPropertyName("storageId")]
    public string StorageId { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = "";
    [JsonPropertyName("controlMessageId")]
    public string ControlMessageId { get; set; } = "";
    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; } = "";
    [JsonPropertyName("state")]
    public string State { get; set; } = "open";
    [JsonPropertyName("revision")]
    public long Revision { get; set; }
    [JsonPropertyName("members")]
    public List<StorageMember> Members { get; set; } = [];
    [JsonIgnore] public string CatalogMessageId { get; set; } = "";
}

internal sealed class StorageMember
{
    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = "";
    [JsonPropertyName("pendingVersions")]
    public List<string> PendingVersions { get; set; } = [];
}

internal sealed class StorageReferenceRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("entryType")]
    public string EntryType { get; set; } = "storageReference";
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";
    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; } = "";
    [JsonPropertyName("state")]
    public string State { get; set; } = "live";
    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = "";
    [JsonPropertyName("metadataThreadId")]
    public string MetadataThreadId { get; set; } = "";
    [JsonPropertyName("metadataMessageId")]
    public string MetadataMessageId { get; set; } = "";
    [JsonPropertyName("payloads")]
    public List<StoragePayloadVersion> Payloads { get; set; } = [];
}

internal sealed class StoragePayloadVersion
{
    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = "";
    [JsonPropertyName("versionId")]
    public string VersionId { get; set; } = "";
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = "";
    [JsonPropertyName("storage")]
    public StorageLayout? Storage { get; set; }
    [JsonPropertyName("attachments")]
    public List<AttachmentReference> Attachments { get; set; } = [];
}

internal sealed class StorageCleanupRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("entryType")]
    public string EntryType { get; set; } = "storageCleanup";
    [JsonPropertyName("operationId")]
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = "";
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";
    [JsonPropertyName("metadataThreadId")]
    public string MetadataThreadId { get; set; } = "";
    [JsonPropertyName("metadataMessageId")]
    public string MetadataMessageId { get; set; } = "";
    [JsonPropertyName("payloads")]
    public List<StoragePayloadVersion> Payloads { get; set; } = [];
}

internal sealed class StorageBatchPlan
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("entryType")]
    public string EntryType { get; set; } = "storageBatchPlan";
    [JsonPropertyName("batchId")]
    public string BatchId { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = "";
    [JsonPropertyName("parts")]
    public List<StorageBatchPart> Parts { get; set; } = [];
}

internal sealed class StorageBatchPart
{
    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = "";
    [JsonPropertyName("versionId")]
    public string VersionId { get; set; } = "";
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
    [JsonPropertyName("index")]
    public int Index { get; set; }
    [JsonPropertyName("count")]
    public int Count { get; set; }
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

internal sealed record SharedStorageReservation(string FileId, string VersionId, string ThreadId, StorageLayout Layout);
