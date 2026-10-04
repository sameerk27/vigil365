using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// The opaque, tamper-proof <c>state</c> carried through the Microsoft
/// admin-consent round trip and handed back to the anonymous <c>/consented</c>
/// landing page. It ties the returning redirect to the exact ClientTenant row
/// the operator started onboarding, and it is signed with its own Data Protection
/// purpose and expires after 30 minutes, so a forged or stale <c>state</c> cannot
/// flip a tenant's consent flag. It deliberately does not go through <see cref="SecretProtector"/>,
/// whose legacy-plaintext fallback would accept a hand-made state. The landing
/// page has no signed-in user to authorise it — this token is the only thing it
/// can trust.
///
/// It also carries the row's current consent nonce (ClientTenant.ConsentNonce),
/// which /consented clears once it records consent: a link works until it has
/// been used successfully once, and is then dead even inside its 30 minutes.
/// </summary>
public static class ConsentState
{
    private const string Purpose = "Vigil365.ConsentState.v1";
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>A fresh nonce for a row that has none.</summary>
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <param name="issuedAt">Now; tests pass an earlier time to get an expired state.</param>
    public static string Encode(IDataProtectionProvider provider, Guid tenantId, string nonce, DateTimeOffset? issuedAt = null)
        => Protector(provider).Protect($"{tenantId:N}.{nonce}", (issuedAt ?? DateTimeOffset.UtcNow) + MaxAge);

    /// <summary>The tenant id and nonce if the state is authentic and fresh; null otherwise.</summary>
    public static (Guid TenantId, string Nonce)? Decode(IDataProtectionProvider provider, string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        try
        {
            // Throws for anything this install did not sign for this purpose
            // (plaintext included) and for a signed state past its expiry.
            var plain = Protector(provider).Unprotect(state, out _);
            var dot = plain.IndexOf('.');
            return dot > 0 && dot < plain.Length - 1 && Guid.TryParseExact(plain[..dot], "N", out var tenantId)
                ? (tenantId, plain[(dot + 1)..])
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="nonce"/> is the row's current one (constant time).</summary>
    public static bool IsCurrent(string? rowNonce, string nonce)
        => !string.IsNullOrEmpty(rowNonce)
           && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(rowNonce), System.Text.Encoding.UTF8.GetBytes(nonce));

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider provider)
        => provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
}
