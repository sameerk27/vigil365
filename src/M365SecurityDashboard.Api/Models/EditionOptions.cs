using System.Security.Claims;

namespace M365SecurityDashboard.Api.Models;

public enum EditionMode
{
    /// <summary>One organisation monitoring its own tenant (Edition 1). The default.</summary>
    Single,

    /// <summary>An MSP monitoring many client tenants (Edition 2).</summary>
    Msp,
}

/// <summary>
/// Which edition this install runs as. Bound from the "Edition" config section and
/// chosen at install time. Defaults to Single so every existing install is
/// unchanged. It gates the MSP surface (Clients, onboarding, consent landing) — it
/// is a configuration choice, not a licence check.
/// </summary>
public sealed class EditionOptions
{
    public const string SectionName = "Edition";

    public EditionMode Mode { get; set; } = EditionMode.Single;

    public bool IsMsp => Mode == EditionMode.Msp;
}

/// <summary>
/// Sign-in is pinned to the operator's own Entra tenant. In MSP mode the app
/// registration is multi-tenant so client tenants can consent to its Graph
/// permissions — which also means a client's users can obtain a token for it.
/// Issuer validation should already reject those, but this is the single most
/// important property of MSP mode, so it is enforced explicitly on the `tid`
/// claim as well rather than left to configuration.
/// </summary>
public static class SignInTenantPin
{
    private const string TenantIdClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    public static bool IsAllowed(ClaimsPrincipal? principal, string? configuredTenantId)
    {
        if (principal is null || string.IsNullOrWhiteSpace(configuredTenantId)) return false;
        var tid = principal.FindFirst("tid")?.Value ?? principal.FindFirst(TenantIdClaim)?.Value;
        return !string.IsNullOrWhiteSpace(tid)
            && string.Equals(tid, configuredTenantId.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
