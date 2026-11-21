using System.Security.Cryptography;
using System.Text;

namespace Organessonium.Services;

internal static class LocalSecretProtector
{
    private const int MaximumPlaintextBytes = 16 * 1024;
    private const int MaximumProtectedBytes = 64 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Organessonium/local-settings/v1");

    public static string Protect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return "";
        }

        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            if (plaintext.Length > MaximumPlaintextBytes)
            {
                throw new InvalidOperationException("A credencial local excede o tamanho máximo permitido.");
            }

            var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static bool TryUnprotect(string? protectedValue, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return false;
        }

        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedValue);
            if (protectedBytes.Length == 0 || protectedBytes.Length > MaximumProtectedBytes)
            {
                return false;
            }

            plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            if (plaintext.Length == 0 || plaintext.Length > MaximumPlaintextBytes)
            {
                return false;
            }

            value = Encoding.UTF8.GetString(plaintext);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or PlatformNotSupportedException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (protectedBytes != null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (plaintext != null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }
}
