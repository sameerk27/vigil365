using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Encrypts sensitive values (SMTP password, webhook URLs, Graph client secret) at
/// rest. Uses ASP.NET Core Data Protection, which works on Windows, Linux, and in
/// containers; the key ring is persisted to disk (mount it as a volume in Docker)
/// so secrets survive restarts. Legacy Windows DPAPI values (prefix "dpapi:") are
/// still readable on Windows for backward compatibility; new values are written
/// with the cross-platform "dp:" prefix.
/// </summary>
public sealed class SecretProtector
{
    private const string DpPrefix = "dp:";       // cross-platform Data Protection
    private const string DpapiPrefix = "dpapi:";  // legacy Windows DPAPI
    private readonly IDataProtector _protector;
    private readonly ILogger<SecretProtector> _logger;

    public SecretProtector(IDataProtectionProvider provider, ILogger<SecretProtector> logger)
    {
        _protector = provider.CreateProtector("Vigil365.Secrets.v1");
        _logger = logger;
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (plaintext.StartsWith(DpPrefix, StringComparison.Ordinal) ||
            plaintext.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return plaintext; // already protected
        try
        {
            return DpPrefix + _protector.Protect(plaintext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protect failed; storing value unprotected");
            return plaintext;
        }
    }

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;

        if (stored.StartsWith(DpPrefix, StringComparison.Ordinal))
        {
            try { return _protector.Unprotect(stored[DpPrefix.Length..]); }
            catch (Exception ex) { _logger.LogWarning(ex, "Unprotect failed; returning empty"); return null; }
        }

        // Legacy Windows DPAPI value — only decryptable on the original Windows host.
        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                var cipher = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(cipher, null, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Legacy DPAPI Unprotect failed; returning empty"); return null; }
        }

        return stored; // legacy plaintext
    }
}
