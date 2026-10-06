using System.Text.Json.Serialization;

namespace Organessonium.Services.MetadataEncryption;

public sealed class MetadataEncryptedEnvelope
{
    [JsonPropertyName("format")] public string Format { get; set; } = MetadataCryptoParameters.Format;
    [JsonPropertyName("version")] public int Version { get; set; } = MetadataCryptoParameters.Version;
    [JsonPropertyName("encrypted")] public bool Encrypted { get; set; } = true;
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = MetadataCryptoParameters.Algorithm;
    [JsonPropertyName("kdf")] public string Kdf { get; set; } = MetadataCryptoParameters.Kdf;
    [JsonPropertyName("keyMode")] public string KeyMode { get; set; } = "standard";
    [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("keyCheck")] public string KeyCheck { get; set; } = "";
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = "";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
}
