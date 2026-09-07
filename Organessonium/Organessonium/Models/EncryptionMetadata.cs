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

    [JsonPropertyName("keyId")]
    public string? KeyId { get; set; }

    public EncryptionMetadata Clone()
    {
        return new EncryptionMetadata
        {
            Version = Version,
            Mode = Mode,
            Salt = Salt,
            KeyId = KeyId
        };
    }
}

internal static class EncryptionMetadataCodec
{
    public static string EncodeSalt(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] DecodeSalt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("Salt de criptografia vazio.");
        }

        var normalized = value;
        if (normalized.Length % 4 == 1 || normalized.Any(character => !IsBase64UrlCharacter(character)))
        {
            throw new FormatException("Salt de criptografia não está em Base64URL sem padding.");
        }

        var padding = (4 - normalized.Length % 4) % 4;
        var standard = normalized.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(standard + new string('=', padding));
    }

    private static bool IsBase64UrlCharacter(char value)
    {
        return value is >= 'A' and <= 'Z' ||
               value is >= 'a' and <= 'z' ||
               value is >= '0' and <= '9' ||
               value is '-' or '_';
    }
}

public sealed record EncryptedPayloadContext(
    string FileId,
    EncryptionMetadata Encryption,
    string Role,
    int ChunkIndex,
    int ChunkCount = 1)
{
    public EncryptedPayloadContext WithChunkIndex(int chunkIndex)
    {
        return this with { ChunkIndex = chunkIndex };
    }

    public EncryptedPayloadContext WithChunkCount(int chunkCount)
    {
        return this with { ChunkCount = chunkCount };
    }
}
