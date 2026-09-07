using System.Text.Json.Serialization;

namespace Organessonium.Models;

public static class EncryptionModes
{
    public const string Standard = "standard";
    public const string Custom = "custom";

    public static string Normalize(string? value)
    {
        return string.Equals(value, Custom, StringComparison.OrdinalIgnoreCase)
            ? Custom
            : Standard;
    }

    public static bool IsSupported(string? value)
    {
        return string.Equals(value, Standard, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, Custom, StringComparison.OrdinalIgnoreCase);
    }
}

public static class EncryptedPayloadRoles
{
    public const string Original = "original";
    public const string Preview = "preview";
    public const string Video480p = "video480p";
    public const string MarkdownImage = "markdownImage";
}

public sealed class EncryptionMetadata
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = EncryptionModes.Standard;

    [JsonPropertyName("salt")]
    public string Salt { get; set; } = "";

    public EncryptionMetadata Clone()
    {
        return new EncryptionMetadata
        {
            Version = Version,
            Mode = Mode,
            Salt = Salt
        };
    }
}

public sealed record EncryptedPayloadContext(
    string FileId,
    EncryptionMetadata Encryption,
    string Role,
    int ChunkIndex)
{
    public EncryptedPayloadContext WithChunkIndex(int chunkIndex)
    {
        return this with { ChunkIndex = chunkIndex };
    }
}
