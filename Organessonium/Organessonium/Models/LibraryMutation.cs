using System.Text.Json.Serialization;

namespace Organessonium.Models;

internal enum LibraryMutationKind { Move, Trash, Restore, Delete }

internal sealed record LibraryChange(
    [property: JsonPropertyName("source")] AppEntry Source,
    [property: JsonPropertyName("destination")] AppEntry? Destination);

// AppEntry omits payload details from JSON sent to the WebView. Persist the
// complete file metadata separately so a staged relocation can be replayed.
internal sealed record LibraryRelocation(AppEntry Source, AppEntry Destination, string SourceChannelId, FileMetadata? FilePayload);

internal sealed record LibraryMutation(LibraryMutationKind Kind, List<LibraryChange> Changes, string Timestamp)
{
    public bool ReadWholeTrash { get; set; }
    public List<LibraryRelocation>? Relocations { get; set; }
    public bool SerialPublished { get; set; }
    [JsonIgnore]
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
