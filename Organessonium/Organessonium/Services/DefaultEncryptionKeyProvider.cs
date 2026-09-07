using System.Security.Cryptography;
using System.Text;

namespace Organessonium.Services;

public sealed class DefaultEncryptionKeyProvider
{
    private const int Iterations = 600_000;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("Organessonium/default-master/v1");
    private byte[]? _masterKey;

    public byte[] GetMasterKey()
    {
        return _masterKey ??= Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes("matehfields"),
            Salt,
            Iterations,
            HashAlgorithmName.SHA256,
            32);
    }
}
