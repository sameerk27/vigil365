namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// The opaque, tamper-proof <c>state</c> carried through the Microsoft
/// admin-consent round trip and handed back to the anonymous <c>/consented</c>
/// landing page. It ties the returning redirect to the exact ClientTenant row
/// the operator started onboarding, and it is signed (via <see cref="SecretProtector"/>)
/// and time-boxed so a forged or replayed <c>state</c> cannot flip a tenant's
/// consent flag. The landing page has no signed-in user to authorise it — this
/// token is the only thing it can trust.
/// </summary>
public static class ConsentState
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    public static string Encode(SecretProtector protector, Guid tenantId)
        => protector.Protect($"{tenantId:N}.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}")
           ?? throw new InvalidOperationException("Could not sign the consent state.");

    /// <summary>Returns the tenant id if the state is authentic and fresh; null otherwise.</summary>
    public static Guid? Decode(SecretProtector protector, string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        var plain = protector.Unprotect(state);
        if (plain is null) return null;

        var parts = plain.Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var tenantId)
            || !long.TryParse(parts[1], out var issuedUnix))
            return null;

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(issuedUnix);
        return age >= TimeSpan.Zero && age <= MaxAge ? tenantId : null;
    }
}
