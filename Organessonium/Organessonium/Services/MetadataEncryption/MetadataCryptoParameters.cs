using System.Text;

namespace Organessonium.Services.MetadataEncryption;

public static class MetadataCryptoParameters
{
    public const string Format = "organessonium-metadata";
    public const int Version = 1;
    public const string Algorithm = "AES-256-GCM";
    public const string Kdf = "HKDF-SHA256";
    public const int KeyBytes = 32;
    public const int SaltBytes = 16;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int KeyCheckBytes = 16;
    public const int MaxEnvelopeBytes = DiscordLimits.AttachmentBytes;
    public const int MaxPlaintextBytes = (MaxEnvelopeBytes - 1024) * 3 / 4;
    internal static ReadOnlySpan<byte> KeyInfo => "organessonium/metadata-key/v1"u8;
    internal static ReadOnlySpan<byte> KeyCheckInfo => "organessonium/metadata-key-check/v1"u8;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Aad(MetadataEncryptedEnvelope envelope) => Utf8.GetBytes(string.Join('\n',
        Format, "1", "true", Algorithm, Kdf, envelope.KeyMode, envelope.Salt, envelope.Nonce, envelope.KeyCheck));
}
