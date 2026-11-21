using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace Organessonium.Services;

public interface IEncryptionSecretStore
{
    string ReadEncryptionSecret(string key);
    void WriteEncryptionSecret(string key, string value);
}

internal static class EncryptionSettingKeys
{
    public const string ProtectedMasterKey = "customEncryptionMasterKeyProtectedV1";
    public const string KdfSalt = "customEncryptionKdfSaltV1";
    public const string KdfIterations = "customEncryptionKdfIterationsV1";
}

public enum CustomKeyImportKind
{
    PasswordProtected,
    Legacy
}

public sealed class RecoveryPasswordRequiredException : InvalidOperationException
{
    public RecoveryPasswordRequiredException()
        : base("Informe a senha do arquivo de recuperação.")
    {
    }
}

public sealed class CustomEncryptionKeyProvider
{
    private const int PasswordIterations = 600_000;
    private const int MasterKeyBytes = 32;
    private const int RecoverySaltBytes = 16;
    private const int RecoveryNonceBytes = 12;
    private const int RecoveryTagBytes = 16;
    private const int MaximumRecoveryPasswordCharacters = 1024;
    private const uint MinimumRecoveryIterations = 100_000;
    private const uint MaximumRecoveryIterations = 2_000_000;
    private const int MaximumRecoveryFileBytes = 4096;
    private const int ProtectedHeaderBytes = 25;
    private const int ProtectedFileBytes = ProtectedHeaderBytes + RecoveryNonceBytes + MasterKeyBytes + RecoveryTagBytes;
    private const int LegacyFileBytes = 6 + MasterKeyBytes + 32;
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("Organessonium/custom-master/v1");
    private static readonly byte[] LegacyRecoveryMagic = "OEK1"u8.ToArray();
    private static readonly byte[] ProtectedRecoveryMagic = "OEK2"u8.ToArray();
    private byte[]? _masterKey;
    private readonly IEncryptionSecretStore _store;

    public CustomEncryptionKeyProvider(IEncryptionSecretStore store)
    {
        _store = store;
    }

    public bool HasStoredKey => !string.IsNullOrWhiteSpace(_store.ReadEncryptionSecret(EncryptionSettingKeys.ProtectedMasterKey));

    public void ClearCachedKey()
    {
        var masterKey = Interlocked.Exchange(ref _masterKey, null);
        if (masterKey != null)
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public void ConfigureFromPassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new InvalidOperationException("Informe uma senha para a chave personalizada.");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        byte[] masterKey;
        try
        {
            masterKey = Rfc2898DeriveBytes.Pbkdf2(
                passphraseBytes,
                salt,
                PasswordIterations,
                HashAlgorithmName.SHA256,
                MasterKeyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }

        try
        {
            StoreMasterKey(masterKey, salt, PasswordIterations);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw;
        }
    }

    public byte[] GetMasterKey()
    {
        if (_masterKey != null)
        {
            return _masterKey;
        }

        var protectedValue = _store.ReadEncryptionSecret(EncryptionSettingKeys.ProtectedMasterKey);
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            throw new CustomEncryptionKeyUnavailableException("A chave personalizada não está disponível neste dispositivo.");
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedValue);
            var masterKey = ProtectedData.Unprotect(protectedBytes, ProtectionEntropy, DataProtectionScope.CurrentUser);
            if (masterKey.Length != MasterKeyBytes)
            {
                CryptographicOperations.ZeroMemory(masterKey);
                throw new CryptographicException("Material de chave personalizado inválido.");
            }

            _masterKey = masterKey;
            return _masterKey;
        }
        catch (CustomEncryptionKeyUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new CustomEncryptionKeyUnavailableException("Não foi possível abrir a chave personalizada neste dispositivo.", ex);
        }
    }

    public static void ValidateRecoveryPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new InvalidOperationException("A senha de recuperação deve ter pelo menos 8 caracteres.");
        }

        if (password.Length > MaximumRecoveryPasswordCharacters)
        {
            throw new InvalidOperationException("A senha de recuperação é longa demais.");
        }
    }

    /// <summary>
    /// Grava a chave mestra em um envelope autenticado protegido por senha. O
    /// arquivo antigo sem senha não é mais produzido.
    /// </summary>
    public async Task ExportAsync(string path, string password, CancellationToken cancellationToken = default)
    {
        ValidateRecoveryPassword(password);

        var masterKey = GetMasterKey();
        var header = new byte[ProtectedHeaderBytes];
        ProtectedRecoveryMagic.CopyTo(header, 0);
        header[4] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), PasswordIterations);
        var salt = RandomNumberGenerator.GetBytes(RecoverySaltBytes);
        salt.CopyTo(header, 9);

        var nonce = RandomNumberGenerator.GetBytes(RecoveryNonceBytes);
        var ciphertext = new byte[MasterKeyBytes];
        var tag = new byte[RecoveryTagBytes];
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var derivedKey = Array.Empty<byte>();
        var payload = new byte[ProtectedFileBytes];
        string? temporaryPath = null;
        try
        {
            derivedKey = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                PasswordIterations,
                HashAlgorithmName.SHA256,
                MasterKeyBytes);
            using (var aes = new AesGcm(derivedKey, RecoveryTagBytes))
            {
                aes.Encrypt(nonce, masterKey, ciphertext, tag, header);
            }

            header.CopyTo(payload, 0);
            nonce.CopyTo(payload, ProtectedHeaderBytes);
            ciphertext.CopyTo(payload, ProtectedHeaderBytes + RecoveryNonceBytes);
            tag.CopyTo(payload, ProtectedHeaderBytes + RecoveryNonceBytes + MasterKeyBytes);

            var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            temporaryPath = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temporaryPath, payload, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }

            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>
    /// Importa o formato autenticado com senha ou, somente para compatibilidade,
    /// o formato legado OEK1 que não possui proteção por senha.
    /// </summary>
    public async Task<CustomKeyImportKind> ImportAsync(string path, string? password, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length > MaximumRecoveryFileBytes)
        {
            throw new InvalidOperationException("O arquivo de recuperação não possui um formato compatível.");
        }

        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        try
        {
            if (payload.Length > MaximumRecoveryFileBytes)
            {
                throw new InvalidOperationException("O arquivo de recuperação não possui um formato compatível.");
            }

            if (payload.AsSpan().StartsWith(ProtectedRecoveryMagic))
            {
                ImportProtected(payload, password);
                return CustomKeyImportKind.PasswordProtected;
            }

            if (payload.AsSpan().StartsWith(LegacyRecoveryMagic))
            {
                ImportLegacy(payload);
                return CustomKeyImportKind.Legacy;
            }

            throw new InvalidOperationException("O arquivo de recuperação não possui um formato compatível.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private void ImportProtected(ReadOnlySpan<byte> payload, string? password)
    {
        if (payload.Length != ProtectedFileBytes || payload[4] != 1)
        {
            throw new InvalidOperationException("O arquivo de recuperação não possui um formato compatível.");
        }

        var iterations = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(5, 4));
        if (iterations < MinimumRecoveryIterations || iterations > MaximumRecoveryIterations)
        {
            throw new InvalidOperationException("O arquivo de recuperação não possui um formato compatível.");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new RecoveryPasswordRequiredException();
        }

        if (password.Length > MaximumRecoveryPasswordCharacters)
        {
            throw new InvalidOperationException("A senha de recuperação é longa demais.");
        }

        var header = payload.Slice(0, ProtectedHeaderBytes);
        var nonce = payload.Slice(ProtectedHeaderBytes, RecoveryNonceBytes);
        var ciphertext = payload.Slice(ProtectedHeaderBytes + RecoveryNonceBytes, MasterKeyBytes);
        var tag = payload.Slice(ProtectedHeaderBytes + RecoveryNonceBytes + MasterKeyBytes, RecoveryTagBytes);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var derivedKey = Array.Empty<byte>();
        var masterKey = new byte[MasterKeyBytes];
        try
        {
            try
            {
                derivedKey = Rfc2898DeriveBytes.Pbkdf2(
                    passwordBytes,
                    header.Slice(9, RecoverySaltBytes),
                    (int)iterations,
                    HashAlgorithmName.SHA256,
                    MasterKeyBytes);

                using (var aes = new AesGcm(derivedKey, RecoveryTagBytes))
                {
                    aes.Decrypt(nonce, ciphertext, tag, masterKey, header);
                }
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Senha incorreta ou arquivo de recuperação adulterado.");
            }

            StoreMasterKey(masterKey, RandomNumberGenerator.GetBytes(16), 0);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    private void ImportLegacy(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != LegacyFileBytes || payload[4] != 1 || payload[5] != MasterKeyBytes)
        {
            throw new InvalidOperationException("O arquivo de recuperação não possui um formato compatível.");
        }

        var masterKey = payload.Slice(6, MasterKeyBytes).ToArray();
        var body = payload.Slice(0, 6 + MasterKeyBytes);
        var expectedTag = HMACSHA256.HashData(masterKey, body);
        var suppliedTag = payload.Slice(6 + MasterKeyBytes, expectedTag.Length);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, suppliedTag))
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(expectedTag);
            throw new InvalidOperationException("O arquivo de recuperação está corrompido.");
        }

        CryptographicOperations.ZeroMemory(expectedTag);
        try
        {
            StoreMasterKey(masterKey, RandomNumberGenerator.GetBytes(16), 0);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw;
        }
    }

    private void StoreMasterKey(byte[] masterKey, byte[] kdfSalt, int iterations)
    {
        if (masterKey.Length != MasterKeyBytes)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw new ArgumentException("A chave personalizada deve possuir 256 bits.", nameof(masterKey));
        }

        var protectedBytes = ProtectedData.Protect(masterKey, ProtectionEntropy, DataProtectionScope.CurrentUser);
        try
        {
            _store.WriteEncryptionSecret(EncryptionSettingKeys.KdfSalt, Convert.ToBase64String(kdfSalt));
            _store.WriteEncryptionSecret(EncryptionSettingKeys.KdfIterations, iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _store.WriteEncryptionSecret(EncryptionSettingKeys.ProtectedMasterKey, Convert.ToBase64String(protectedBytes));

            if (_masterKey != null)
            {
                CryptographicOperations.ZeroMemory(_masterKey);
            }
            _masterKey = masterKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }
}

public sealed class CustomEncryptionKeyUnavailableException : InvalidOperationException
{
    public CustomEncryptionKeyUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
