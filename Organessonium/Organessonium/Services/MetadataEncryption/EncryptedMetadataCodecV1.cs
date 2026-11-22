using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services.MetadataEncryption;

internal sealed class EncryptedMetadataCodecV1(Func<string, byte[]> resolveMasterKey,
    Func<IEnumerable<byte[]>>? historicalKeys = null)
{
    internal string Encode(string json, string mode)
    {
        var plaintext = MetadataCryptoParameters.Utf8.GetBytes(json);
        if (plaintext.Length > MetadataCryptoParameters.MaxPlaintextBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new MetadataCodecException(MetadataError.TooLarge);
        }
        var salt = RandomNumberGenerator.GetBytes(MetadataCryptoParameters.SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(MetadataCryptoParameters.NonceBytes);
        try { return Encrypt(plaintext, mode, salt, nonce); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    // Explicit deterministic inputs are restricted to the test assembly, never used by production writes.
    internal string Encrypt(ReadOnlySpan<byte> plaintext, string mode, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> nonce)
    {
        if (salt.Length != MetadataCryptoParameters.SaltBytes || nonce.Length != MetadataCryptoParameters.NonceBytes ||
            mode is not (EncryptionModes.Standard or EncryptionModes.Custom))
            throw new MetadataCodecException(MetadataError.InvalidFormat);
        var key = Derive(mode, salt);
        try
        {
            var envelope = new MetadataEncryptedEnvelope
            {
                KeyMode = mode, Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce),
                KeyCheck = Convert.ToBase64String(KeyCheck(key))
            };
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[MetadataCryptoParameters.TagBytes];
            using var aes = new AesGcm(key, MetadataCryptoParameters.TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, MetadataCryptoParameters.Aad(envelope));
            envelope.Ciphertext = Convert.ToBase64String(ciphertext);
            envelope.Tag = Convert.ToBase64String(tag);
            var result = JsonSerializer.Serialize(envelope, JsonOptions.Web);
            if (MetadataCryptoParameters.Utf8.GetByteCount(result) > MetadataCryptoParameters.MaxEnvelopeBytes)
                throw new MetadataCodecException(MetadataError.TooLarge);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal string Decode(JsonElement root)
    {
        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        string[] required = ["format", "version", "encrypted", "algorithm", "kdf", "keyMode", "salt", "nonce", "keyCheck", "ciphertext", "tag"];
        if (required.Any(n => !names.Contains(n))) throw new MetadataCodecException(MetadataError.MissingField);
        if (names.Count != required.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new MetadataCodecException(MetadataError.InvalidFormat);
        if (!root.GetProperty("version").TryGetInt32(out var version)) throw new MetadataCodecException(MetadataError.InvalidFormat);
        if (version != MetadataCryptoParameters.Version) throw new MetadataCodecException(MetadataError.UnsupportedVersion);
        MetadataEncryptedEnvelope envelope;
        try { envelope = root.Deserialize<MetadataEncryptedEnvelope>(JsonOptions.Web)!; }
        catch (JsonException) { throw new MetadataCodecException(MetadataError.InvalidFormat); }
        if (envelope.Format != MetadataCryptoParameters.Format || !envelope.Encrypted ||
            envelope.Algorithm != MetadataCryptoParameters.Algorithm || envelope.Kdf != MetadataCryptoParameters.Kdf ||
            envelope.KeyMode is not (EncryptionModes.Standard or EncryptionModes.Custom))
            throw new MetadataCodecException(MetadataError.InvalidFormat);
        var salt = Base64(envelope.Salt, MetadataCryptoParameters.SaltBytes);
        var nonce = Base64(envelope.Nonce, MetadataCryptoParameters.NonceBytes);
        var keyCheck = Base64(envelope.KeyCheck, MetadataCryptoParameters.KeyCheckBytes);
        var ciphertext = Base64(envelope.Ciphertext);
        var tag = Base64(envelope.Tag, MetadataCryptoParameters.TagBytes);
        if (ciphertext.Length > MetadataCryptoParameters.MaxPlaintextBytes) throw new MetadataCodecException(MetadataError.TooLarge);
        var key = ResolveReadKey(envelope.KeyMode, salt, keyCheck);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, MetadataCryptoParameters.TagBytes);
            try { aes.Decrypt(nonce, ciphertext, tag, plaintext, MetadataCryptoParameters.Aad(envelope)); }
            catch (AuthenticationTagMismatchException) { throw new MetadataCodecException(MetadataError.AuthenticationFailed); }
            string json;
            try { json = MetadataCryptoParameters.Utf8.GetString(plaintext); }
            catch (DecoderFallbackException) { throw new MetadataCodecException(MetadataError.InvalidPlaintextJson); }
            MetadataCodec.ValidateJson(json, MetadataError.InvalidPlaintextJson);
            return json;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] Derive(string mode, ReadOnlySpan<byte> salt)
    {
        byte[] master;
        try { master = resolveMasterKey(mode); }
        catch (CustomEncryptionKeyUnavailableException) { throw new MetadataCodecException(MetadataError.KeyUnavailable); }
        return DeriveAndClear(master, salt);
    }

    private byte[] ResolveReadKey(string mode, byte[] salt, byte[] expectedCheck)
    {
        var unavailable = false;
        byte[]? key = null;
        try { key = Derive(mode, salt); }
        catch (MetadataCodecException error) when (error.Error == MetadataError.KeyUnavailable &&
            mode == EncryptionModes.Custom && historicalKeys != null) { unavailable = true; }
        if (key != null && MatchesKey(key, expectedCheck)) return key;

        if (mode == EncryptionModes.Custom && historicalKeys != null)
        {
            foreach (var master in historicalKeys())
            {
                var candidate = DeriveAndClear(master, salt);
                if (MatchesKey(candidate, expectedCheck)) return candidate;
                unavailable = false;
            }
        }
        throw new MetadataCodecException(unavailable ? MetadataError.KeyUnavailable : MetadataError.KeyMismatch);
    }

    private static bool MatchesKey(byte[] key, byte[] expectedCheck)
    {
        if (CryptographicOperations.FixedTimeEquals(expectedCheck, KeyCheck(key))) return true;
        CryptographicOperations.ZeroMemory(key);
        return false;
    }

    private static byte[] DeriveAndClear(byte[] master, ReadOnlySpan<byte> salt)
    {
        if (master.Length != MetadataCryptoParameters.KeyBytes)
        { CryptographicOperations.ZeroMemory(master); throw new MetadataCodecException(MetadataError.KeyUnavailable); }
        try
        {
            var key = new byte[MetadataCryptoParameters.KeyBytes];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, master, key, salt, MetadataCryptoParameters.KeyInfo);
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    private static byte[] KeyCheck(ReadOnlySpan<byte> key) =>
        HMACSHA256.HashData(key, MetadataCryptoParameters.KeyCheckInfo)[..MetadataCryptoParameters.KeyCheckBytes];

    private static byte[] Base64(string? value, int? length = null)
    {
        if (value == null || value.Any(c => char.IsWhiteSpace(c))) throw new MetadataCodecException(MetadataError.InvalidBase64);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException) { throw new MetadataCodecException(MetadataError.InvalidBase64); }
        if (Convert.ToBase64String(bytes) != value) throw new MetadataCodecException(MetadataError.InvalidBase64);
        if (length.HasValue && bytes.Length != length) throw new MetadataCodecException(MetadataError.InvalidFormat);
        return bytes;
    }
}
