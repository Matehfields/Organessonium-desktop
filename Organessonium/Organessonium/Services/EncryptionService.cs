using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class EncryptionService
{
    public const int EnvelopeHeaderBytes = 54;
    public const int AuthenticationTagBytes = 16;
    public const int EnvelopeOverheadBytes = EnvelopeHeaderBytes + AuthenticationTagBytes;
    public const int MaximumPlaintextPayloadBytes = DiscordLimits.PlaintextChunkBytes;
    private const int FileSaltBytes = 16;
    private const int NonceBytes = 12;
    private static readonly byte[] EnvelopeMagic = "ORGAESGC"u8.ToArray();
    private readonly DefaultEncryptionKeyProvider _defaultKeyProvider;
    private readonly CustomEncryptionKeyProvider _customKeyProvider;

    public EncryptionService(DefaultEncryptionKeyProvider defaultKeyProvider, CustomEncryptionKeyProvider customKeyProvider)
    {
        _defaultKeyProvider = defaultKeyProvider;
        _customKeyProvider = customKeyProvider;
    }

    public bool HasCustomKey => _customKeyProvider.HasStoredKey;

    internal byte[] CopyMasterKeyForMetadata(string mode) => (mode == EncryptionModes.Custom
        ? _customKeyProvider.GetMasterKey() : _defaultKeyProvider.GetMasterKey()).ToArray();

    public void ClearCustomKey()
    {
        _customKeyProvider.ClearCachedKey();
    }

    public static bool LooksLikeEncryptedEnvelope(ReadOnlySpan<byte> payload)
    {
        return payload.Length >= EnvelopeMagic.Length &&
               payload[..EnvelopeMagic.Length].SequenceEqual(EnvelopeMagic);
    }

    public EncryptionMetadata CreateFileEncryption(string mode)
    {
        return new EncryptionMetadata
        {
            Version = EncryptionMetadata.CurrentVersion,
            Mode = EncryptionModes.Normalize(mode),
            Salt = EncryptionMetadataCodec.EncodeSalt(RandomNumberGenerator.GetBytes(FileSaltBytes))
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
                await EncryptFileAsync(files[index], target, new EncryptedPayloadContext(fileId, encryption, role, index, files.Count), cancellationToken);
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

    public async Task EncryptFileRangeAsync(
        FileInfo source,
        long offset,
        int length,
        FileInfo destination,
        EncryptedPayloadContext context,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var plaintext = GC.AllocateUninitializedArray<byte>(length);
        var encrypted = GC.AllocateUninitializedArray<byte>(checked(length + EnvelopeOverheadBytes));
        try
        {
            await EncryptFileRangeIntoAsync(
                source,
                offset,
                length,
                plaintext,
                encrypted,
                context,
                cancellationToken);
            destination.Directory?.Create();
            await File.WriteAllBytesAsync(destination.FullName, encrypted, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    internal async Task<int> EncryptFileRangeIntoAsync(
        FileInfo source,
        long offset,
        int length,
        Memory<byte> plaintextBuffer,
        Memory<byte> encryptedBuffer,
        EncryptedPayloadContext context,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (plaintextBuffer.Length < length)
        {
            throw new ArgumentException("O buffer de plaintext é menor que o chunk solicitado.", nameof(plaintextBuffer));
        }

        var encryptedLength = checked(length + EnvelopeOverheadBytes);
        if (encryptedBuffer.Length < encryptedLength)
        {
            throw new ArgumentException("O buffer de ciphertext é menor que o envelope solicitado.", nameof(encryptedBuffer));
        }

        try
        {
            await using var input = new FileStream(
                source.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = offset;
            var readTotal = 0;
            while (readTotal < length)
            {
                var read = await input.ReadAsync(plaintextBuffer.Slice(readTotal, length - readTotal), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("O arquivo mudou enquanto o chunk era preparado.");
                }

                readTotal += read;
            }

            return EncryptBytesInto(plaintextBuffer.Span[..length], encryptedBuffer.Span, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBuffer.Span[..length]);
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
        var output = GC.AllocateUninitializedArray<byte>(checked(EnvelopeHeaderBytes + plaintext.Length + AuthenticationTagBytes));
        try
        {
            EncryptBytesInto(plaintext, output, context);
            return output;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
    }

    internal int EncryptBytesInto(ReadOnlySpan<byte> plaintext, Span<byte> destination, EncryptedPayloadContext context)
    {
        var outputLength = checked(EnvelopeHeaderBytes + plaintext.Length + AuthenticationTagBytes);
        if (destination.Length < outputLength)
        {
            throw new ArgumentException("O buffer de destino não comporta o envelope criptografado.", nameof(destination));
        }

        var roleCode = RoleCode(context.Role);
        ValidateChunkPosition(context.ChunkIndex, context.ChunkCount);
        var salt = DecodeFileSalt(context.Encryption);
        try
        {
            var fileKey = DeriveFileKey(context.FileId, context.Encryption, salt);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
                var header = new byte[EnvelopeHeaderBytes];
                try
                {
                    EnvelopeMagic.CopyTo(header, 0);
                    header[8] = EncryptionMetadata.CurrentVersion;
                    header[9] = roleCode;
                    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(10, 4), checked((uint)context.ChunkIndex + 1U));
                    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(14, 4), checked((uint)context.ChunkCount));
                    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(18, 8), checked((ulong)plaintext.Length));
                    salt.CopyTo(header.AsSpan(26, FileSaltBytes));
                    nonce.CopyTo(header.AsSpan(42, NonceBytes));

                    using var aes = new AesGcm(fileKey, AuthenticationTagBytes);
                    header.CopyTo(destination);
                    aes.Encrypt(
                        nonce,
                        plaintext,
                        destination.Slice(EnvelopeHeaderBytes, plaintext.Length),
                        destination.Slice(EnvelopeHeaderBytes + plaintext.Length, AuthenticationTagBytes),
                        header);
                    return outputLength;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(header);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public byte[] DecryptBytes(ReadOnlySpan<byte> payload, EncryptedPayloadContext context)
    {
        if (payload.Length < EnvelopeOverheadBytes || !LooksLikeEncryptedEnvelope(payload))
        {
            throw new EncryptedPayloadFormatException("O payload criptografado não possui um envelope válido.");
        }

        if (payload[8] != EncryptionMetadata.CurrentVersion)
        {
            throw new EncryptedPayloadFormatException("A versão do payload criptografado não é suportada.");
        }

        var roleCode = payload[9];
        if (roleCode is < 1 or > 3 || roleCode != RoleCode(context.Role))
        {
            throw new EncryptedPayloadFormatException("O papel do payload criptografado não corresponde ao esperado.");
        }

        var chunkIndex = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(10, 4));
        var chunkCount = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(14, 4));
        ValidateChunkPosition(chunkIndex, chunkCount);
        ValidateChunkPosition(context.ChunkIndex, context.ChunkCount);
        if (chunkIndex != checked((uint)context.ChunkIndex + 1U) ||
            chunkCount != checked((uint)context.ChunkCount))
        {
            throw new EncryptedPayloadFormatException("A posição do chunk criptografado não corresponde ao esperado.");
        }

        var plaintextLength = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(18, 8));
        var ciphertextLength = payload.Length - EnvelopeHeaderBytes - AuthenticationTagBytes;
        if (plaintextLength > int.MaxValue || ciphertextLength < 0 || plaintextLength != (ulong)ciphertextLength)
        {
            throw new EncryptedPayloadFormatException("O payload criptografado está truncado ou possui tamanho inválido.");
        }

        var salt = DecodeFileSalt(context.Encryption);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(payload.Slice(26, FileSaltBytes), salt))
            {
                throw new EncryptedPayloadFormatException("O salt do envelope não corresponde aos metadados do arquivo.");
            }

            var fileKey = DeriveFileKey(context.FileId, context.Encryption, salt);
            var plaintext = new byte[ciphertextLength];
            try
            {
                using var aes = new AesGcm(fileKey, AuthenticationTagBytes);
                aes.Decrypt(
                    payload.Slice(42, NonceBytes),
                    payload.Slice(EnvelopeHeaderBytes, ciphertextLength),
                    payload.Slice(EnvelopeHeaderBytes + ciphertextLength, AuthenticationTagBytes),
                    plaintext,
                    payload[..EnvelopeHeaderBytes]);
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
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private byte[] DeriveFileKey(string fileId, EncryptionMetadata encryption, byte[] salt)
    {
        if (string.IsNullOrWhiteSpace(fileId) || encryption.Version != EncryptionMetadata.CurrentVersion ||
            !EncryptionModes.IsSupported(encryption.Mode))
        {
            throw new EncryptedPayloadFormatException("Os metadados de criptografia do arquivo são inválidos.");
        }

        var masterKey = EncryptionModes.Normalize(encryption.Mode) == EncryptionModes.Custom
            ? _customKeyProvider.GetMasterKey()
            : _defaultKeyProvider.GetMasterKey();
        var fileKey = new byte[32];
        var encodedFileId = Encoding.UTF8.GetBytes(fileId.Trim());
        var domain = Encoding.UTF8.GetBytes("organessonium/file-key/v1");
        var info = new byte[domain.Length + 1 + 4 + encodedFileId.Length];
        try
        {
            domain.CopyTo(info, 0);
            BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(domain.Length + 1, 4), checked((uint)encodedFileId.Length));
            encodedFileId.CopyTo(info, domain.Length + 1 + 4);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, fileKey, salt, info);
            return fileKey;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(fileKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedFileId);
            CryptographicOperations.ZeroMemory(domain);
            CryptographicOperations.ZeroMemory(info);
        }
    }

    private static byte[] DecodeFileSalt(EncryptionMetadata encryption)
    {
        if (encryption.Version != EncryptionMetadata.CurrentVersion || !EncryptionModes.IsSupported(encryption.Mode))
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

        if (salt.Length != FileSaltBytes)
        {
            CryptographicOperations.ZeroMemory(salt);
            throw new EncryptedPayloadFormatException("O salt de criptografia do arquivo deve conter 16 bytes.");
        }

        return salt;
    }

    private static byte RoleCode(string role)
    {
        return role switch
        {
            EncryptedPayloadRoles.Original => 1,
            EncryptedPayloadRoles.Preview => 2,
            EncryptedPayloadRoles.Video480p => 3,
            EncryptedPayloadRoles.MarkdownImage => 2,
            _ => throw new EncryptedPayloadFormatException("O papel do payload criptografado é inválido.")
        };
    }

    private static void ValidateChunkPosition(int chunkIndex, int chunkCount)
    {
        if (chunkIndex < 0 || chunkCount <= 0 || chunkIndex >= chunkCount)
        {
            throw new EncryptedPayloadFormatException("A posição do chunk criptografado é inválida.");
        }
    }

    private static void ValidateChunkPosition(uint chunkIndex, uint chunkCount)
    {
        if (chunkIndex == 0 || chunkCount == 0 || chunkIndex > chunkCount)
        {
            throw new EncryptedPayloadFormatException("A posição do chunk criptografado é inválida.");
        }
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
    private const string FilePrefix = "file:";
    private const string RolePrefix = "role:";
    private const string ChunkPrefix = "chunk:";
    private const string EncryptionMarker = "encryption:v1";

    public static string Message(EncryptedPayloadContext context, int totalParts)
    {
        return $"{FilePrefix}{context.FileId} {RolePrefix}{context.Role} {ChunkPrefix}{context.ChunkIndex + 1}/{totalParts} {EncryptionMarker}";
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
        var parts = (message ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !parts[0].StartsWith(FilePrefix, StringComparison.Ordinal) ||
            !parts[1].StartsWith(RolePrefix, StringComparison.Ordinal) ||
            !parts[2].StartsWith(ChunkPrefix, StringComparison.Ordinal) ||
            !parts[3].Equals(EncryptionMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var fileId = parts[0][FilePrefix.Length..];
        var role = parts[1][RolePrefix.Length..];
        var chunk = parts[2][ChunkPrefix.Length..];
        if (string.IsNullOrWhiteSpace(fileId) || string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        var slash = chunk.IndexOf('/');
        if (slash <= 0 ||
            !int.TryParse(chunk[..slash], out var oneBasedIndex) ||
            !int.TryParse(chunk[(slash + 1)..], out var total) ||
            oneBasedIndex <= 0 || total <= 0 || oneBasedIndex > total)
        {
            return false;
        }

        payload = new ParsedEncryptedPayload(fileId, role, oneBasedIndex - 1, total);
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
