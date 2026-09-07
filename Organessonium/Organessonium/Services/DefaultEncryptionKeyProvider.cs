using System.Security.Cryptography;
using System.Text;

namespace Organessonium.Services;

public sealed class DefaultEncryptionKeyProvider
{
    private static readonly byte[] LogicalIdentity = Encoding.UTF8.GetBytes("matehfields");
    private static readonly byte[] MasterSalt = Encoding.UTF8.GetBytes("organessonium/master-key/salt/v1");
    private static readonly byte[] MasterInfo = Encoding.UTF8.GetBytes("organessonium/master-key/aes-256-gcm/v1");
    private byte[]? _masterKey;

    public byte[] GetMasterKey()
    {
        if (_masterKey != null)
        {
            return _masterKey;
        }

        var masterKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, LogicalIdentity, masterKey, MasterSalt, MasterInfo);
        return _masterKey = masterKey;
    }
}
