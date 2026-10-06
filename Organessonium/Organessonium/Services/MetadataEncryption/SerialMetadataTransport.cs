using System.Text.Json;

namespace Organessonium.Services.MetadataEncryption;

public sealed class SerialMetadataTransport(IMetadataCodec codec)
{
    public byte[] Encode(byte[] serial) => MetadataCryptoParameters.Utf8.GetBytes(codec.Encode(
        JsonSerializer.Serialize(new { schemaVersion = 1, entryType = "folderSerialData", data = Convert.ToBase64String(serial) }, JsonOptions.Web)));

    public byte[] Decode(byte[] stored)
    {
        if (stored.AsSpan().StartsWith("OSER"u8)) return stored; // OSER v1-v4 legacy, no write branch.
        string json;
        try { json = MetadataCryptoParameters.Utf8.GetString(stored); }
        catch (System.Text.DecoderFallbackException) { throw new MetadataCodecException(MetadataError.InvalidFormat); }
        if (!codec.IsEncrypted(json)) throw new MetadataCodecException(MetadataError.InvalidFormat);
        using var document = JsonDocument.Parse(codec.Decode(json));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var parsedVersion) || parsedVersion != 1 ||
            !root.TryGetProperty("entryType", out var type) || type.GetString() != "folderSerialData" ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            throw new MetadataCodecException(MetadataError.InvalidFormat);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data.GetString()!); }
        catch (FormatException) { throw new MetadataCodecException(MetadataError.InvalidBase64); }
        if (Convert.ToBase64String(bytes) != data.GetString()) throw new MetadataCodecException(MetadataError.InvalidBase64);
        return bytes;
    }
}
