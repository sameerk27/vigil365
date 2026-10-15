using System.Security.Claims;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace M365SecurityDashboard.Api.Data.Tenancy;

/// <summary>
/// Which tenants a signed-in user may work in. One rule, used by the request
/// middleware (may this user select tenant X?), the tenant switcher (which
/// tenants to offer) and the rollup (which tenants to aggregate):
///
///   Admin  → every active tenant.
///   Others → the active tenants assigned to them (UserTenantAssignments).
///
/// Cached per user for a short time; assignment changes invalidate it.
/// </summary>
public sealed class TenantAccess(AppDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static string Key(string email) => "tenancy:permitted:" + email.ToLowerInvariant();

    public async Task<IReadOnlyList<ClientTenant>> PermittedTenantsAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var email = AuthHelpers.GetEmail(user);
        if (string.IsNullOrEmpty(email)) return [];

        if (cache.TryGetValue(Key(email), out IReadOnlyList<ClientTenant>? cached) && cached is not null)
            return cached;

        var active = db.ClientTenants.AsNoTracking().Where(t => t.IsActive);
        List<ClientTenant> permitted;
        if (user.IsInRole(AppRoles.Admin))
        {
            permitted = await active.OrderBy(t => t.Name).ToListAsync(ct);
        }
        else
        {
            var assigned = db.UserTenantAssignments.AsNoTracking()
                .Where(a => a.UserEmail == email).Select(a => a.TenantId);
            permitted = await active.Where(t => assigned.Contains(t.Id)).OrderBy(t => t.Name).ToListAsync(ct);
        }

        cache.Set(Key(email), (IReadOnlyList<ClientTenant>)permitted, Ttl);
        return permitted;
    }

    public async Task<bool> CanSelectAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct)
        => (await PermittedTenantsAsync(user, ct)).Any(t => t.Id == tenantId);

    /// <summary>After assignments change for a user.</summary>
    public static void Invalidate(IMemoryCache cache, string email) => cache.Remove(Key(email));
}
