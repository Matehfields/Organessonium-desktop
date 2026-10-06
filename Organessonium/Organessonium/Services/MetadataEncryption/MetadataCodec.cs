using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services.MetadataEncryption;

public interface IMetadataCodec
{
    string Encode(string plaintextJson);
    string Decode(string storedJson);
    bool IsEncrypted(string storedJson);
}

public sealed class MetadataCodec : IMetadataCodec
{
    private readonly EncryptedMetadataCodecV1 _v1;
    private readonly Func<string> _writeMode;
    public MetadataCodec(Func<string, byte[]> resolveMasterKey, Func<string>? writeMode = null)
    {
        _v1 = new(resolveMasterKey);
        _writeMode = writeMode ?? (() => EncryptionModes.Standard);
    }
    public static MetadataCodec Standard() => new(_ => new DefaultEncryptionKeyProvider().GetMasterKey().ToArray());
    public static MetadataCodec ForEncryption(EncryptionService encryption, Func<string>? writeMode = null) =>
        new(encryption.CopyMasterKeyForMetadata, writeMode);
    public static MetadataCodec ForStore(SettingsStore store, Func<string>? writeMode = null) => new(mode =>
    {
        if (mode == EncryptionModes.Standard) return new DefaultEncryptionKeyProvider().GetMasterKey().ToArray();
        // Resolve the current DPAPI record so importing a recovery key cannot leave a stale metadata key.
        var custom = new CustomEncryptionKeyProvider(store);
        try { return custom.GetMasterKey().ToArray(); }
        finally { custom.ClearCachedKey(); }
    }, writeMode ?? (() => store.Load().EncryptionMode));

    public string Encode(string plaintextJson)
    {
        ValidateJson(plaintextJson, MetadataError.InvalidPlaintextJson);
        return _v1.Encode(plaintextJson, _writeMode());
    }

    public string Decode(string storedJson)
    {
        if (MetadataCryptoParameters.Utf8.GetByteCount(storedJson) > MetadataCryptoParameters.MaxEnvelopeBytes)
            throw new MetadataCodecException(MetadataError.TooLarge);
        using var document = Parse(storedJson, MetadataError.InvalidFormat);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new MetadataCodecException(MetadataError.InvalidFormat);
        if (DeclaresEncryption(root))
        {
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
                format.GetString() != MetadataCryptoParameters.Format) throw new MetadataCodecException(MetadataError.InvalidFormat);
            if (!root.TryGetProperty("version", out var version)) throw new MetadataCodecException(MetadataError.MissingField);
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
                throw new MetadataCodecException(MetadataError.InvalidFormat);
            return number switch
            {
                MetadataCryptoParameters.Version => _v1.Decode(root),
                _ => throw new MetadataCodecException(MetadataError.UnsupportedVersion)
            };
        }
        // Legacy JSON is read-only. There is no legacy encoder or crypto-error fallback.
        return storedJson;
    }

    public bool IsEncrypted(string storedJson)
    {
        using var document = Parse(storedJson, MetadataError.InvalidFormat);
        return document.RootElement.ValueKind == JsonValueKind.Object && DeclaresEncryption(document.RootElement);
    }

    private static bool DeclaresEncryption(JsonElement root) =>
        (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String &&
            format.GetString() == MetadataCryptoParameters.Format) || root.TryGetProperty("encrypted", out _) ||
        root.TryGetProperty("ciphertext", out _);

    internal static void ValidateJson(string json, MetadataError error)
    {
        using var document = Parse(json, error);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new MetadataCodecException(error);
    }
    private static JsonDocument Parse(string json, MetadataError error)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { throw new MetadataCodecException(error); }
    }
}
