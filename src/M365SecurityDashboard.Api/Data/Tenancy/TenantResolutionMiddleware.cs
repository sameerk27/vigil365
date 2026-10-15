using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Data.Tenancy;

/// <summary>
/// Sets the request's tenant. Runs after authentication so role claims exist.
///
/// Resolution order:
///   1. <c>X-Vigil-Tenant</c> header naming a tenant id — the tenant switcher.
///      The signed-in user must be permitted to see it (<see cref="TenantAccess"/>:
///      Admins every active tenant, others their assigned ones).
///   2. Otherwise, if the install has exactly one active tenant (every Edition 1
///      install), that tenant. Cached briefly — it is read on every API call.
///      In MSP mode a signed-in user must also be permitted it: being the only
///      client does not make it theirs.
///   2b. Otherwise, if the signed-in user is permitted exactly one tenant, that one.
///   3. Otherwise nothing is set, and any endpoint that touches tenant-scoped
///      data fails closed with <see cref="TenantRequiredException"/>, mapped
///      here to a 400 telling the caller to pick a tenant.
///
/// API-token callers (/api/siem) resolve separately: a client-restricted token
/// is pinned to its client while that client is active; an install-wide token
/// may name any active tenant or fall back to step 2. Anonymous install-level
/// probes (/health) read across tenants explicitly and need no tenant.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Vigil-Tenant";
    private const string SoleTenantCacheKey = "tenancy:sole-active-tenant";
    private static readonly TimeSpan SoleTenantTtl = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(HttpContext ctx, TenantContext tenant, AppDbContext db, IMemoryCache cache, TenantAccess access, Services.ApiTokenService tokens, IOptions<EditionOptions> edition)
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            var header = ctx.Request.Headers[HeaderName].ToString();
            if (ctx.User.Identity?.IsAuthenticated != true && ctx.Request.Path.StartsWithSegments("/api/siem"))
            {
                // API-token callers (SIEM endpoints). The endpoint checks scope; here
                // we only resolve the tenant: a token restricted to one client is
                // pinned to it (and may not ask for another); an install-wide token
                // may select any tenant, or rely on the sole-tenant fallback below.
                var token = await tokens.LookupAsync(ReadApiToken(ctx), ctx.RequestAborted);
                if (token is not null)
                {
                    if (token.TenantId is Guid pinned)
                    {
                        if (!string.IsNullOrWhiteSpace(header) && (!Guid.TryParse(header, out var asked) || asked != pinned))
                        {
                            await Reject(ctx, StatusCodes.Status403Forbidden, "This API token is restricted to one tenant.");
                            return;
                        }
                        // Deactivating (or purging) the client stops its tokens too.
                        if (!await db.ClientTenants.AsNoTracking().AnyAsync(t => t.Id == pinned && t.IsActive, ctx.RequestAborted))
                        { await Reject(ctx, StatusCodes.Status403Forbidden, "Unknown or inactive tenant."); return; }
                        tenant.Set(pinned);
                        await Next(ctx); return;
                    }
                    if (!string.IsNullOrWhiteSpace(header))
                    {
                        if (!Guid.TryParse(header, out var requested)) { await Reject(ctx, StatusCodes.Status400BadRequest, $"{HeaderName} must be a tenant id."); return; }
                        if (!await db.ClientTenants.AsNoTracking().AnyAsync(t => t.Id == requested && t.IsActive, ctx.RequestAborted))
                        { await Reject(ctx, StatusCodes.Status403Forbidden, "Unknown or inactive tenant."); return; }
                        tenant.Set(requested);
                        await Next(ctx); return;
                    }
                }
                header = ""; // no valid token: fall through to the sole-tenant rule; the endpoint returns 401
            }
            else if (!string.IsNullOrWhiteSpace(header) && ctx.User.Identity?.IsAuthenticated != true)
            {
                await Reject(ctx, StatusCodes.Status401Unauthorized, $"Sign in to use {HeaderName}.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(header))
            {
                if (!Guid.TryParse(header, out var requested))
                {
                    await Reject(ctx, StatusCodes.Status400BadRequest, $"{HeaderName} must be a tenant id.");
                    return;
                }
                if (!await access.CanSelectAsync(ctx.User, requested, ctx.RequestAborted))
                {
                    // One answer for "does not exist", "inactive" and "not yours":
                    // an unauthorised caller must not learn which it was.
                    await Reject(ctx, StatusCodes.Status403Forbidden, "You do not have access to that tenant.");
                    return;
                }
                tenant.Set(requested);
            }
            else
            {
                // With exactly one tenant there is nothing to choose. In a single
                // organisation install there is nothing to leak either; in MSP mode a
                // signed-in user still goes through TenantAccess, so an unassigned
                // Analyst or a never-provisioned Viewer does not get the only client.
                // (An unauthenticated caller here has an install-wide API token or
                // reaches only anonymous endpoints, which check their own token.)
                var signedIn = ctx.User.Identity?.IsAuthenticated == true;
                var sole = await ResolveSoleActiveTenantAsync(db, cache, ctx.RequestAborted);
                if (sole is Guid id && (!signedIn || !edition.Value.IsMsp || await access.CanSelectAsync(ctx.User, id, ctx.RequestAborted)))
                    tenant.Set(id);
                else if (signedIn)
                {
                    // Several tenants exist but this user may see only one: no
                    // switcher is shown to them, so choose it.
                    var permitted = await access.PermittedTenantsAsync(ctx.User, ctx.RequestAborted);
                    if (permitted.Count == 1) tenant.Set(permitted[0].Id);
                }
            }
        }

        await Next(ctx);
    }

    private async Task Next(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (TenantRequiredException ex) when (!ctx.Response.HasStarted)
        {
            await Reject(ctx, StatusCodes.Status400BadRequest,
                $"{ex.Message} Send the {HeaderName} header to choose one.");
        }
    }

    private static string? ReadApiToken(HttpContext ctx)
    {
        var apiKey = ctx.Request.Headers["X-Api-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(apiKey)) return apiKey.Trim();
        var auth = ctx.Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : null;
    }

    /// <summary>The one active tenant when there is exactly one; null otherwise.</summary>
    public static async Task<Guid?> ResolveSoleActiveTenantAsync(AppDbContext db, IMemoryCache cache, CancellationToken ct)
    {
        if (cache.TryGetValue(SoleTenantCacheKey, out Guid? cached)) return cached;

        var active = await db.ClientTenants.AsNoTracking()
            .Where(t => t.IsActive).Select(t => t.Id).Take(2).ToListAsync(ct);
        Guid? sole = active.Count == 1 ? active[0] : null;
        cache.Set(SoleTenantCacheKey, sole, SoleTenantTtl);
        return sole;
    }

    /// <summary>Call after adding, activating or deactivating a tenant.</summary>
    public static void InvalidateSoleTenant(IMemoryCache cache) => cache.Remove(SoleTenantCacheKey);

    private static Task Reject(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { ok = false, message });
    }
}
