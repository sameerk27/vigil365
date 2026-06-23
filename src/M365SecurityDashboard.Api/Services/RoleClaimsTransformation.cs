using System.Security.Claims;
using M365SecurityDashboard.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// After the Microsoft token is validated (which proves WHO the user is), this
/// attaches the user's Vigil365 role (WHAT they can do) as a role claim, read from
/// our own AppUsers table. The role policies (RequireAdmin / RequireAnalyst) then
/// work unchanged. Roles are managed in-app by an Admin — never in Entra ID.
///
/// Read-only: user creation and bootstrap happen once in GET /api/auth/me. If a
/// user has no row yet, they get Viewer (least privilege) so reads still work.
/// </summary>
public sealed class RoleClaimsTransformation(AppDbContext db) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true }) return principal;

        // Avoid re-adding on repeated invocations within a request.
        if (principal.HasClaim(c => c.Type == ClaimTypes.Role)) return principal;

        var email = AuthHelpers.GetEmail(principal);
        if (string.IsNullOrEmpty(email)) return principal;

        var role = await db.AppUsers
            .Where(u => u.Email == email)
            .Select(u => u.Role)
            .FirstOrDefaultAsync() ?? Models.AppRoles.Viewer;

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
