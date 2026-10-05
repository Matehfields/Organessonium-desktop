using System.Text.Json.Serialization;

namespace Organessonium.Models;

// Optional extension of schemaVersion 2. Ordinary/mobile entries need none of these fields.
public sealed class SyncMetadata
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;
    [JsonPropertyName("syncFolderId")]
    public string SyncFolderId { get; set; } = "";
    [JsonPropertyName("relativePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RelativePath { get; set; }
    [JsonPropertyName("hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; set; }
    [JsonPropertyName("modifiedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedUtc { get; set; }
    [JsonPropertyName("revision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Revision { get; set; }
    [JsonPropertyName("revisionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RevisionId { get; set; }
    [JsonPropertyName("deviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; set; }
    [JsonPropertyName("deviceName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; set; }
    [JsonPropertyName("state")]
    public string State { get; set; } = "active";
    [JsonPropertyName("deletedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeletedUtc { get; set; }
    [JsonPropertyName("canonicalFolderId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CanonicalFolderId { get; set; }
}

public sealed class SyncDeviceMetadata
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "";
}

public sealed class SyncFolderBinding
{
    public int Version { get; set; } = 1;
    public string SyncFolderId { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string CloudFolderId { get; set; } = "";
    public string CloudPath { get; set; } = "";
    public bool Automatic { get; set; } = true;
    public bool Bidirectional { get; set; } = true;
    public bool Paused { get; set; }
    public bool AutomaticRemoteUpdates { get; set; }
    // Conflicts default to asking, even if remote-only updates are automatic.
    public string ConflictPolicy { get; set; } = "ask";
    public string DeletionPolicy { get; set; } = "ask";
    public bool StartWithWindows { get; set; }
}

public sealed class SyncFileState
{
    public string RelativePath { get; set; } = "";
    public string FileId { get; set; } = "";
    public string Kind { get; set; } = "file";
    public string BaseRevisionId { get; set; } = "";
    public string BaseHash { get; set; } = "";
    public bool BaseDeleted { get; set; }
    public bool LocalExists { get; set; }
    public long LocalSize { get; set; }
    public long LocalWriteTicks { get; set; }
    public string LocalHash { get; set; } = "";
    public string IgnoredRevisionId { get; set; } = "";
}

// Append-only commits in the existing folder index. Sibling commits are conflicts,
// never resolved by a clock. Payload uses the existing FileMetadata/chunk protocol.
public sealed class SyncRevision
{
    public int SchemaVersion { get; set; } = 2;
    public string EntryType { get; set; } = "syncRevision";
    public int SyncVersion { get; set; } = 1;
    public string SyncFolderId { get; set; } = "";
    public string FileId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Kind { get; set; } = "file";
    public long SizeBytes { get; set; }
    public string Hash { get; set; } = "";
    public string ModifiedUtc { get; set; } = "";
    public long Revision { get; set; }
    public string RevisionId { get; set; } = Guid.NewGuid().ToString("N");
    public List<string> Parents { get; set; } = [];
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string State { get; set; } = "active";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeletedUtc { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileMetadata? Payload { get; set; }
    [JsonIgnore] public string MessageId { get; set; } = "";
    [JsonIgnore] public bool IsDeleted => State == "deleted";
}

public sealed class SyncIssue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SyncFolderId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Kind { get; set; } = "remoteUpdate";
    public string Message { get; set; } = "";
    public string LocalHash { get; set; } = "";
    public string LocalModifiedUtc { get; set; } = "";
    public List<SyncRevision> Remote { get; set; } = [];
    public string Resolution { get; set; } = "";
    public bool Remember { get; set; }
}

public sealed record SyncFolderStatus(string SyncFolderId, string Name, string LocalPath,
    string CloudFolderId, string State, int Pending, int Conflicts, bool Paused, string Message);
public sealed record SyncTransferProgress(string Id, string Name, string Direction, double Percent, string Status);
public sealed record SyncLocalFile(string RelativePath, string Kind, long Size, long WriteTicks, string Hash);
public sealed record SyncScan(IReadOnlyDictionary<string, SyncLocalFile> Entries,
    IReadOnlySet<string> BusyPaths, bool Complete);
public sealed record SyncCloudSnapshot(IReadOnlyList<SyncRevision> Revisions,
    IReadOnlyDictionary<string, IReadOnlyList<SyncRevision>> Heads)
{
    public IReadOnlyDictionary<string, IReadOnlyList<SyncRevision>> HeadsByFileId { get; } = Heads.Values.SelectMany(h => h)
        .GroupBy(h => h.FileId, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => (IReadOnlyList<SyncRevision>)g.ToList(), StringComparer.Ordinal);
}
