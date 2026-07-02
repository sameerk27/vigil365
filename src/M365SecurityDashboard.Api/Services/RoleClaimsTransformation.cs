using System.Security.Claims;
using M365SecurityDashboard.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// After the Microsoft token is validated (which proves WHO the user is), this
/// attaches the user's Vigil365 role (WHAT they can do) as a role claim, read from
/// our own AppUsers table. The role policies (RequireAdmin / RequireAnalyst) then
/// work unchanged. Roles are managed in-app by an Admin — never in Entra ID.
///
/// Read-only: user creation and bootstrap happen once in GET /api/auth/me. If a
/// user has no row yet, they get Viewer (least privilege) so reads still work.
///
/// Roles are cached for a short TTL to avoid a DB round-trip on every request.
/// Role-change/removal endpoints evict the entry so changes apply immediately;
/// the TTL only bounds staleness across multiple app instances.
/// </summary>
public sealed class RoleClaimsTransformation(AppDbContext db, IMemoryCache cache) : IClaimsTransformation
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Cache key for a user's role; also used by endpoints to evict on change.</summary>
    public static string RoleCacheKey(string email) => $"approle:{email}";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true }) return principal;

        // Avoid re-adding on repeated invocations within a request.
        if (principal.HasClaim(c => c.Type == ClaimTypes.Role)) return principal;

        var email = AuthHelpers.GetEmail(principal);
        if (string.IsNullOrEmpty(email)) return principal;

        var role = await cache.GetOrCreateAsync(RoleCacheKey(email), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await db.AppUsers
                .Where(u => u.Email == email)
                .Select(u => u.Role)
                .FirstOrDefaultAsync() ?? Models.AppRoles.Viewer;
        }) ?? Models.AppRoles.Viewer;

        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        principal.AddIdentity(identity);
        return principal;
    }
}

/// <summary>Shared helpers for pulling identity out of a validated principal.</summary>
public static class AuthHelpers
{
    /// <summary>The signed-in user's email / UPN, lower-cased, or empty string.</summary>
    public static string GetEmail(ClaimsPrincipal principal) =>
        (principal.FindFirst("preferred_username")?.Value
         ?? principal.FindFirst(ClaimTypes.Upn)?.Value
         ?? principal.FindFirst(ClaimTypes.Email)?.Value
         ?? "").Trim().ToLowerInvariant();

    public static string GetDisplayName(ClaimsPrincipal principal) =>
        principal.FindFirst("name")?.Value
        ?? principal.FindFirst(ClaimTypes.Name)?.Value
        ?? "";
}
