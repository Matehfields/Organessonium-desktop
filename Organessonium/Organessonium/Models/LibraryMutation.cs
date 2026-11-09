using System.Text.Json.Serialization;

namespace Organessonium.Models;

internal enum LibraryMutationKind { Move, Trash, Restore, Delete, CreateFolder }

internal sealed record LibraryChange(
    [property: JsonPropertyName("source")] AppEntry Source,
    [property: JsonPropertyName("destination")] AppEntry? Destination,
    [property: JsonPropertyName("creatingFolder")] bool CreatingFolder = false);

// AppEntry omits payload details from JSON sent to the WebView. Persist the
// complete file metadata separately so a staged relocation can be replayed.
internal sealed record LibraryRelocation(AppEntry Source, AppEntry Destination, string SourceChannelId, FileMetadata? FilePayload);

internal sealed record FolderCreationCheckpoint(string PostTitle)
{
    public bool Attempted { get; set; }
    public string? ThreadId { get; set; }
    public AppEntry? Published { get; set; }
}

internal sealed record LibraryMutation(LibraryMutationKind Kind, List<LibraryChange> Changes, string Timestamp)
{
    public FolderCreationCheckpoint? FolderCreation { get; init; }
    public bool ReadWholeTrash { get; set; }
    public List<LibraryRelocation>? Relocations { get; set; }
    public bool SerialPublished { get; set; }
    [JsonIgnore]
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
