namespace Organessonium.Models;

public enum FolderSerialKind
{
    Folder = 0,
    Favorites = 1
}

public sealed class FolderSerialAggregate
{
    public long TotalItems { get; init; }
    public long FileCount { get; init; }
    public long FolderCount { get; init; }
    public long TotalBytes { get; init; }
    public long ImageCount { get; init; }
    public long VideoCount { get; init; }
    public IReadOnlyDictionary<string, long> TypeCounts { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
}

public sealed class FolderSerialEntry
{
    public string Id { get; init; } = "";
    public string NamesMessageId { get; init; } = "";
    public string ParentFolderId { get; init; } = "root";
    public string Name { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public bool IsFolder { get; init; }
    public string MimeType { get; init; } = "";
    public DateTimeOffset UploadedAt { get; init; }
    public string PreviewMessageId { get; init; } = "";
    public EncryptionMetadata? Encryption { get; init; }
    public string FolderColor { get; init; } = "#C4C7C5";
    public string? DeviceType { get; init; }
    public bool Favorite { get; init; }
}

public sealed class FolderSerial
{
    public string FolderId { get; init; } = "";
    public string ParentFolderId { get; init; } = "root";
    public string FolderName { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; }
    public FolderSerialAggregate Aggregate { get; init; } = new();
    public IReadOnlyList<FolderSerialEntry> Children { get; init; } = [];
    public FolderSerialKind Kind { get; init; } = FolderSerialKind.Folder;
}
