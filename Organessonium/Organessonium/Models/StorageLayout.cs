using System.IO;
using System.Text.Json.Serialization;

namespace Organessonium.Models;

public sealed record StorageLayout
{
    [JsonPropertyName("version")] public int Version { get; init; } = 2;
    [JsonPropertyName("mode")] public string Mode { get; init; } = "shared";
    [JsonPropertyName("storageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StorageId { get; init; }
    [JsonIgnore] public bool Shared => Mode == "shared";

    public void Validate()
    {
        if (Version != 2 || Mode is not ("shared" or "dedicated") ||
            (Shared && !Guid.TryParseExact(StorageId, "N", out _)))
            throw new InvalidDataException("O layout de armazenamento não é válido ou ainda não é suportado.");
    }
}

public sealed record AttachmentReference
{
    [JsonPropertyName("messageId")] public string MessageId { get; init; } = "";
    [JsonPropertyName("attachmentId")] public string AttachmentId { get; init; } = "";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MessageId) || string.IsNullOrWhiteSpace(AttachmentId))
            throw new InvalidDataException("A referência da miniatura está incompleta.");
    }
}
