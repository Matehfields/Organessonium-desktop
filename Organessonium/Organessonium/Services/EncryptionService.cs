using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class EncryptionService
{
    public const int EnvelopeHeaderBytes = 26;
    public const int AuthenticationTagBytes = 16;
    public const int EnvelopeOverheadBytes = EnvelopeHeaderBytes + AuthenticationTagBytes;
    public const int MaximumPlaintextPayloadBytes = 10 * 1024 * 1024 - EnvelopeOverheadBytes;
    private static readonly byte[] EnvelopeMagic = "OESB"u8.ToArray();
    private readonly DefaultEncryptionKeyProvider _defaultKeyProvider;
    private readonly CustomEncryptionKeyProvider _customKeyProvider;

    public EncryptionService(DefaultEncryptionKeyProvider defaultKeyProvider, CustomEncryptionKeyProvider customKeyProvider)
    {
        _defaultKeyProvider = defaultKeyProvider;
        _customKeyProvider = customKeyProvider;
    }

    public bool HasCustomKey => _customKeyProvider.HasStoredKey;

    public EncryptionMetadata CreateFileEncryption(string mode)
    {
        return new EncryptionMetadata
        {
            Version = EncryptionMetadata.CurrentVersion,
            Mode = EncryptionModes.Normalize(mode),
            Salt = EncryptionMetadataCodec.EncodeSalt(RandomNumberGenerator.GetBytes(16))
        };
    }

    public void ConfigureCustomKey(string passphrase)
    {
        if (HasCustomKey)
        {
            throw new InvalidOperationException("Já existe uma chave personalizada neste dispositivo. Ative-a ou importe uma chave de recuperação.");
        }

        _customKeyProvider.ConfigureFromPassphrase(passphrase);
    }

    public Task ExportCustomKeyAsync(string path, CancellationToken cancellationToken = default)
    {
        return _customKeyProvider.ExportAsync(path, cancellationToken);
    }

    public Task ImportCustomKeyAsync(string path, CancellationToken cancellationToken = default)
    {
        return _customKeyProvider.ImportAsync(path, cancellationToken);
    }

    public async Task<EncryptedPayloadStaging> StageFilesAsync(
        IReadOnlyList<FileInfo> files,
        string fileId,
        EncryptionMetadata encryption,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            throw new ArgumentException("Nenhum payload para criptografar.", nameof(files));
        }

        var directory = Directory.CreateTempSubdirectory("organessonium_encrypted_");
        var staged = new List<FileInfo>(files.Count);
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = new FileInfo(Path.Combine(directory.FullName, EncryptedPayloadProtocol.FileName(role, index, files.Count)));
                await EncryptFileAsync(files[index], target, new EncryptedPayloadContext(fileId, encryption, role, index), cancellationToken);
                staged.Add(target);
            }

            return new EncryptedPayloadStaging(directory, staged);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public async Task EncryptFileAsync(
        FileInfo source,
        FileInfo destination,
        EncryptedPayloadContext context,
        CancellationToken cancellationToken = default)
    {
        var plaintext = await File.ReadAllBytesAsync(source.FullName, cancellationToken);
        try
        {
            var encrypted = EncryptBytes(plaintext, context);
            try
            {
                destination.Directory?.Create();
                await File.WriteAllBytesAsync(destination.FullName, encrypted, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task DecryptFileAsync(
        FileInfo source,
        FileInfo destination,
        EncryptedPayloadContext context,
        CancellationToken cancellationToken = default)
    {
        var encrypted = await File.ReadAllBytesAsync(source.FullName, cancellationToken);
        try
        {
            var plaintext = DecryptBytes(encrypted, context);
            try
            {
                destination.Directory?.Create();
                await File.WriteAllBytesAsync(destination.FullName, plaintext, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public byte[] EncryptBytes(ReadOnlySpan<byte> plaintext, EncryptedPayloadContext context)
    {
        var fileKey = DeriveFileKey(context.FileId, context.Encryption);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[AuthenticationTagBytes];
            var output = new byte[EnvelopeHeaderBytes + ciphertext.Length + tag.Length];
            EnvelopeMagic.CopyTo(output, 0);
            output[4] = EncryptionMetadata.CurrentVersion;
            output[5] = 0;
            nonce.CopyTo(output, 6);
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(18, 8), checked((ulong)plaintext.Length));
            using var aes = new AesGcm(fileKey, AuthenticationTagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(context));
            ciphertext.CopyTo(output, EnvelopeHeaderBytes);
            tag.CopyTo(output, EnvelopeHeaderBytes + ciphertext.Length);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    public byte[] DecryptBytes(ReadOnlySpan<byte> payload, EncryptedPayloadContext context)
    {
        if (payload.Length < EnvelopeOverheadBytes ||
            !payload[..EnvelopeMagic.Length].SequenceEqual(EnvelopeMagic))
        {
            throw new EncryptedPayloadFormatException("O payload criptografado não possui um envelope válido.");
        }

        if (payload[4] != EncryptionMetadata.CurrentVersion || payload[5] != 0)
        {
            throw new EncryptedPayloadFormatException("A versão do payload criptografado não é suportada.");
        }

        var plaintextLength = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(18, 8));
        var ciphertextLength = payload.Length - EnvelopeHeaderBytes - AuthenticationTagBytes;
        if (plaintextLength > int.MaxValue || ciphertextLength < 0 || plaintextLength != (ulong)ciphertextLength)
        {
            throw new EncryptedPayloadFormatException("O payload criptografado está truncado ou possui tamanho inválido.");
        }

        var fileKey = DeriveFileKey(context.FileId, context.Encryption);
        var plaintext = new byte[ciphertextLength];
        try
        {
            using var aes = new AesGcm(fileKey, AuthenticationTagBytes);
            aes.Decrypt(
                payload.Slice(6, 12),
                payload.Slice(EnvelopeHeaderBytes, ciphertextLength),
                payload.Slice(EnvelopeHeaderBytes + ciphertextLength, AuthenticationTagBytes),
                plaintext,
                AssociatedData(context));
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new EncryptedPayloadAuthenticationException("O payload criptografado não pôde ser autenticado.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    private byte[] DeriveFileKey(string fileId, EncryptionMetadata encryption)
    {
        if (string.IsNullOrWhiteSpace(fileId) || encryption.Version != EncryptionMetadata.CurrentVersion ||
            !EncryptionModes.IsSupported(encryption.Mode))
        {
            throw new EncryptedPayloadFormatException("Os metadados de criptografia do arquivo são inválidos.");
        }

        byte[] salt;
        try
        {
            salt = EncryptionMetadataCodec.DecodeSalt(encryption.Salt);
        }
        catch (FormatException ex)
        {
            throw new EncryptedPayloadFormatException("O salt de criptografia do arquivo é inválido.", ex);
        }

        if (salt.Length < 16)
        {
            CryptographicOperations.ZeroMemory(salt);
            throw new EncryptedPayloadFormatException("O salt de criptografia do arquivo é inválido.");
        }

        var masterKey = EncryptionModes.Normalize(encryption.Mode) == EncryptionModes.Custom
            ? _customKeyProvider.GetMasterKey()
            : _defaultKeyProvider.GetMasterKey();
        var fileKey = new byte[32];
        var info = Encoding.UTF8.GetBytes("Organessonium/file-key/v1/" + fileId);
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, fileKey, salt, info);
            return fileKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(info);
        }
    }

    private static byte[] AssociatedData(EncryptedPayloadContext context)
    {
        return Encoding.UTF8.GetBytes($"Organessonium/payload/v1|{context.FileId}|{context.Role}|{context.ChunkIndex}");
    }

    private static void TryDeleteDirectory(DirectoryInfo directory)
    {
        try
        {
            if (directory.Exists)
            {
                directory.Delete(true);
            }
        }
        catch
        {
            // Temporary encrypted payloads are removed on a best-effort basis.
        }
    }
}

public sealed class EncryptedPayloadStaging : IDisposable
{
    private readonly DirectoryInfo _directory;
    private bool _disposed;

    public EncryptedPayloadStaging(DirectoryInfo directory, IReadOnlyList<FileInfo> files)
    {
        _directory = directory;
        Files = files;
    }

    public IReadOnlyList<FileInfo> Files { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_directory.Exists)
            {
                _directory.Delete(true);
            }
        }
        catch
        {
            // Temporary encrypted payloads are removed on a best-effort basis.
        }
    }
}

public static class EncryptedPayloadProtocol
{
    private const string Prefix = "OES1";

    public static string Message(EncryptedPayloadContext context, int totalParts)
    {
        return $"{Prefix}|{context.FileId}|{context.Role}|{context.ChunkIndex + 1}/{totalParts}";
    }

    public static string FileName(string role, int index, int totalParts)
    {
        return role switch
        {
            EncryptedPayloadRoles.Original when totalParts > 1 => $"chunk-{index + 1}.bin",
            EncryptedPayloadRoles.Original => "payload.bin",
            EncryptedPayloadRoles.Preview => "preview.bin",
            EncryptedPayloadRoles.Video480p when totalParts > 1 => $"video-preview-{index + 1}.bin",
            EncryptedPayloadRoles.Video480p => "video-preview.bin",
            EncryptedPayloadRoles.MarkdownImage => "image.bin",
            _ => "payload.bin"
        };
    }

    public static bool TryParse(string? message, out ParsedEncryptedPayload payload)
    {
        payload = default;
        var parts = (message ?? "").Split('|');
        if (parts.Length != 4 || !parts[0].Equals(Prefix, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]))
        {
            return false;
        }

        var slash = parts[3].IndexOf('/');
        if (slash <= 0 ||
            !int.TryParse(parts[3][..slash], out var oneBasedIndex) ||
            !int.TryParse(parts[3][(slash + 1)..], out var total) ||
            oneBasedIndex <= 0 || total <= 0 || oneBasedIndex > total)
        {
            return false;
        }

        payload = new ParsedEncryptedPayload(parts[1], parts[2], oneBasedIndex - 1, total);
        return true;
    }
}

public readonly record struct ParsedEncryptedPayload(string FileId, string Role, int ChunkIndex, int TotalParts);

public class EncryptedPayloadException : IOException
{
    public EncryptedPayloadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class EncryptedPayloadFormatException : EncryptedPayloadException
{
    public EncryptedPayloadFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class EncryptedPayloadAuthenticationException : EncryptedPayloadException
{
    public EncryptedPayloadAuthenticationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
