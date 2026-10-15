using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Organessonium.Models;

public sealed record ChunkReference
{
    [JsonPropertyName("index")]
    public int Index { get; init; } = -1;

    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = "";

    [JsonPropertyName("attachmentId")]
    public string AttachmentId { get; init; } = "";

    public static List<ChunkReference> Validate(IReadOnlyList<ChunkReference> references,
        IReadOnlyList<string>? legacyMessages = null, int? totalChunks = null, bool partial = false)
    {
        if (!partial && references.Count == 0)
            throw new InvalidDataException("chunkRefs não contém referências aos chunks.");
        var ordered = references.OrderBy(reference => reference?.Index ?? -1).ToList();
        var identities = new HashSet<(string, string)>();
        var indices = new HashSet<int>();
        for (var position = 0; position < ordered.Count; position++)
        {
            var reference = ordered[position];
            if (reference == null || reference.Index < 0 ||
                (!partial && reference.Index != position) ||
                (totalChunks.HasValue && reference.Index >= totalChunks.Value) ||
                string.IsNullOrWhiteSpace(reference.MessageId) || string.IsNullOrWhiteSpace(reference.AttachmentId) ||
                !indices.Add(reference.Index) || !identities.Add((reference.MessageId, reference.AttachmentId)))
                throw new InvalidDataException("Uma referência de chunk possui índice, mensagem ou attachment inválido.");
        }
        if (legacyMessages is { Count: > 0 } && (legacyMessages.Count != ordered.Count ||
            legacyMessages.Distinct(StringComparer.Ordinal).Count() != legacyMessages.Count ||
            !ordered.Select(reference => reference.MessageId).SequenceEqual(legacyMessages, StringComparer.Ordinal)))
            throw new InvalidDataException("chunks e chunkRefs descrevem layouts incompatíveis.");
        return ordered;
    }

    internal static void ValidateJsonPresence(JsonElement metadata)
    {
        if (metadata.TryGetProperty("storage", out var storage) && (storage.ValueKind != JsonValueKind.Object ||
            !storage.TryGetProperty("version", out var storageVersion) || !storageVersion.TryGetInt32(out _) ||
            !storage.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("storage deve declarar version e mode explicitamente.");
        if (metadata.TryGetProperty("preview", out var thumbnailPreview) && thumbnailPreview.ValueKind == JsonValueKind.Object &&
            thumbnailPreview.TryGetProperty("thumbnailRef", out var thumbnail) &&
            (thumbnail.ValueKind != JsonValueKind.Object || !thumbnail.TryGetProperty("messageId", out var message) ||
                message.ValueKind != JsonValueKind.String || !thumbnail.TryGetProperty("attachmentId", out var attachment) ||
                attachment.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("thumbnailRef deve conter os IDs exatos da mensagem e do attachment.");
        if (metadata.TryGetProperty("chunkRefs", out var references) && references.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("chunkRefs deve ser um array válido, não um valor nulo ou outro formato.");
        if (metadata.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.Object &&
            preview.TryGetProperty("video480p", out var video) && video.ValueKind == JsonValueKind.Object)
            ValidateJsonPresence(video);
    }
}
