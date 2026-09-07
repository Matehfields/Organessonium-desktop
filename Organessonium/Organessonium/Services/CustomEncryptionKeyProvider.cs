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

public sealed class CustomEncryptionKeyProvider
{
    private const int PasswordIterations = 600_000;
    private const int MasterKeyBytes = 32;
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("Organessonium/custom-master/v1");
    private static readonly byte[] RecoveryMagic = "OEK1"u8.ToArray();
    private byte[]? _masterKey;
    private readonly IEncryptionSecretStore _store;

    public CustomEncryptionKeyProvider(IEncryptionSecretStore store)
    {
        _store = store;
    }

    public bool HasStoredKey => !string.IsNullOrWhiteSpace(_store.ReadEncryptionSecret(EncryptionSettingKeys.ProtectedMasterKey));

    public void ConfigureFromPassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new InvalidOperationException("Informe uma senha para a chave personalizada.");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var masterKey = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            MasterKeyBytes);
        StoreMasterKey(masterKey, salt, PasswordIterations);
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

    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        var masterKey = GetMasterKey();
        var body = new byte[RecoveryMagic.Length + 2 + MasterKeyBytes];
        RecoveryMagic.CopyTo(body, 0);
        body[4] = 1;
        body[5] = MasterKeyBytes;
        masterKey.CopyTo(body, 6);
        var tag = HMACSHA256.HashData(masterKey, body);
        var payload = new byte[body.Length + tag.Length];
        body.CopyTo(payload, 0);
        tag.CopyTo(payload, body.Length);
        await File.WriteAllBytesAsync(path, payload, cancellationToken);
        CryptographicOperations.ZeroMemory(tag);
        CryptographicOperations.ZeroMemory(body);
    }

    public async Task ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var payload = await File.ReadAllBytesAsync(path, cancellationToken);
        var expectedLength = RecoveryMagic.Length + 2 + MasterKeyBytes + 32;
        if (payload.Length != expectedLength ||
            !payload.AsSpan(0, RecoveryMagic.Length).SequenceEqual(RecoveryMagic) ||
            payload[4] != 1 ||
            payload[5] != MasterKeyBytes)
        {
            throw new InvalidOperationException("O arquivo de recuperação não possui um formato compatível.");
        }

        var masterKey = payload.AsSpan(6, MasterKeyBytes).ToArray();
        var body = payload.AsSpan(0, RecoveryMagic.Length + 2 + MasterKeyBytes);
        var expectedTag = HMACSHA256.HashData(masterKey, body);
        var suppliedTag = payload.AsSpan(body.Length, expectedTag.Length);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, suppliedTag))
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(expectedTag);
            throw new InvalidOperationException("O arquivo de recuperação está corrompido.");
        }

        CryptographicOperations.ZeroMemory(expectedTag);
        StoreMasterKey(masterKey, RandomNumberGenerator.GetBytes(16), 0);
    }

    private void StoreMasterKey(byte[] masterKey, byte[] kdfSalt, int iterations)
    {
        if (masterKey.Length != MasterKeyBytes)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw new ArgumentException("A chave personalizada deve possuir 256 bits.", nameof(masterKey));
        }

        var protectedBytes = ProtectedData.Protect(masterKey, ProtectionEntropy, DataProtectionScope.CurrentUser);
        _store.WriteEncryptionSecret(EncryptionSettingKeys.ProtectedMasterKey, Convert.ToBase64String(protectedBytes));
        _store.WriteEncryptionSecret(EncryptionSettingKeys.KdfSalt, Convert.ToBase64String(kdfSalt));
        _store.WriteEncryptionSecret(EncryptionSettingKeys.KdfIterations, iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (_masterKey != null)
        {
            CryptographicOperations.ZeroMemory(_masterKey);
        }

        _masterKey = masterKey;
        CryptographicOperations.ZeroMemory(protectedBytes);
    }
}

public sealed class CustomEncryptionKeyUnavailableException : InvalidOperationException
{
    public CustomEncryptionKeyUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
