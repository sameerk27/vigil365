using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Encrypts sensitive values (SMTP password, webhook URLs) at rest using the
/// Windows Data Protection API (DPAPI), machine scope. Ciphertext is bound to
/// this host — a leaked database row cannot be decrypted on another machine.
/// On non-Windows hosts it falls back to returning values unchanged (with a
/// marker) so the app still runs in dev containers; production target is Windows.
/// </summary>
public sealed class SecretProtector(ILogger<SecretProtector> logger)
{
    private const string Prefix = "dpapi:"; // marks a value as DPAPI-encrypted

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext; // already protected
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return plaintext;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var cipher = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DPAPI Protect failed; storing value unprotected");
            return plaintext;
        }
    }

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // legacy plaintext — return as-is

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(cipher, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DPAPI Unprotect failed; returning empty");
            return null;
        }
    }
}
