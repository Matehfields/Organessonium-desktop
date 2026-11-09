using System.Text;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed record MarkdownEncryptedImageReference(
    string FileId,
    EncryptionMetadata Encryption,
    string MessageId,
    string MimeType)
{
    private const string Prefix = "organessonium-image://v1/";

    public string ToUrl()
    {
        return Prefix + string.Join(
            "/",
            FileId,
            EncryptionModes.Normalize(Encryption.Mode),
            ToBase64Url(Encoding.UTF8.GetBytes(Encryption.Salt)),
            MessageId,
            ToBase64Url(Encoding.UTF8.GetBytes(MimeType)));
    }

    public static bool TryParse(string? value, out MarkdownEncryptedImageReference reference)
    {
        reference = default!;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value[Prefix.Length..].Split('/');
        if (parts.Length != 5 || string.IsNullOrWhiteSpace(parts[0]) ||
            !EncryptionModes.IsSupported(parts[1]) || string.IsNullOrWhiteSpace(parts[3]))
        {
            return false;
        }

        try
        {
            var salt = Encoding.UTF8.GetString(FromBase64Url(parts[2])).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var mime = Encoding.UTF8.GetString(FromBase64Url(parts[4]));
            if (EncryptionMetadataCodec.DecodeSalt(salt).Length != 16 || string.IsNullOrWhiteSpace(mime))
            {
                return false;
            }

            reference = new MarkdownEncryptedImageReference(
                parts[0],
                new EncryptionMetadata
                {
                    Version = EncryptionMetadata.CurrentVersion,
                    Mode = parts[1],
                    Salt = salt
                },
                parts[3],
                mime);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
    }
}
