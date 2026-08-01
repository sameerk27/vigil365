using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>Anonymous health probe plus the sign-in surface (/api/auth/config, /api/auth/me).</summary>
public static class AuthHealthEndpoints
{
    public static void MapAuthHealthEndpoints(this WebApplication app)
    {
        // Health endpoint for monitoring / orchestration (Docker HEALTHCHECK, k8s probes,
        // uptime monitors). Reports DB connectivity, Graph configuration, and freshness of
        // the last collection run. No Graph call is made — probes fire frequently and must
        // stay cheap. 200 = healthy/degraded (app can serve traffic), 503 = DB unreachable.
        app.MapGet("/health", async (AppDbContext db, IOptions<GraphOptions> options, CancellationToken ct) =>
        {
            var dbOk = false;
            string? dbError = null;
            object? lastCollection = null;
            var collectionFresh = (bool?)null;

            try
            {
                dbOk = await db.Database.CanConnectAsync(ct);
                if (dbOk)
                {
                    var lastRun = await db.CollectionRuns.AsNoTracking()
                        .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
                    if (lastRun is not null)
                    {
                        var staleAfter = TimeSpan.FromMinutes(Math.Max(options.Value.CollectionIntervalMinutes, 1) * 2);
                        collectionFresh = DateTimeOffset.UtcNow - lastRun.StartedAt <= staleAfter;
                        lastCollection = new
                        {
                            startedAt = lastRun.StartedAt,
                            status = lastRun.Status.ToString(),
                            alertsUpserted = lastRun.AlertsUpserted,
                            fresh = collectionFresh
                        };
                    }
                }
            }
            catch (Exception ex) { dbError = ex.Message; }

            var graphConfigured = options.Value.IsConfigured();
            var status = !dbOk ? "unhealthy"
                : !graphConfigured || collectionFresh == false ? "degraded"
                : "healthy";

            var body = new
            {
                status,
                version = typeof(Program).Assembly.GetName().Version?.ToString(3),
                checks = new
                {
                    database = new { ok = dbOk, error = dbError },
                    graph = new { configured = graphConfigured },
                    collection = lastCollection
                },
                checkedAt = DateTimeOffset.UtcNow
            };
            return dbOk ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        // Public endpoint — returns only the non-secret config needed to initialise MSAL in the browser.
        // The login identity comes from AzureAd (set in appsettings.Production.json / user secrets);
        // fall back to Graph for older single-section setups.
        app.MapGet("/api/auth/config", (IConfiguration config) =>
        {
            string Pick(string azureAdKey, string graphKey)
            {
                var v = config[azureAdKey];
                if (!string.IsNullOrWhiteSpace(v) && !v.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)) return v;
                var g = config[graphKey];
                return (!string.IsNullOrWhiteSpace(g) && !g.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)) ? g : "";
            }
            return Results.Ok(new
            {
                clientId = Pick("AzureAd:ClientId", "Graph:ClientId"),
                tenantId = Pick("AzureAd:TenantId", "Graph:TenantId"),
                redirectUri = config["Auth:RedirectUri"] ?? "http://localhost:5173"
            });
        }).AllowAnonymous();

        // Returns the signed-in user's identity and role, and upserts their AppUsers row.
        // Bootstrap: if Auth:BootstrapAdminEmail is configured, only that email becomes
        // Admin on first sign-in; otherwise the first user to ever sign in becomes Admin.
        // Everyone else defaults to Viewer until an Admin promotes them.
        app.MapGet("/api/auth/me", async (
            System.Security.Claims.ClaimsPrincipal principal, AppDbContext db, IConfiguration config,
            Microsoft.Extensions.Caching.Memory.IMemoryCache cache, AuditLogger audit, CancellationToken ct) =>
        {
            var email = AuthHelpers.GetEmail(principal);
            var displayName = AuthHelpers.GetDisplayName(principal);
            if (string.IsNullOrEmpty(email)) return Results.BadRequest(new { error = "Token has no email claim." });

            var now = DateTimeOffset.UtcNow;
            var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
            var isFirstSignIn = false;
            var isNewSession = false;
            if (user is null)
            {
                var bootstrapEmail = (config["Auth:BootstrapAdminEmail"] ?? "").Trim().ToLowerInvariant();
                string role;
                if (!string.IsNullOrEmpty(bootstrapEmail))
                    role = email == bootstrapEmail ? AppRoles.Admin : AppRoles.Viewer;
                else
                    role = await db.AppUsers.AnyAsync(ct) ? AppRoles.Viewer : AppRoles.Admin;

                user = new AppUser { Email = email, DisplayName = displayName, Role = role, CreatedAt = now, LastSeenAt = now };
                db.AppUsers.Add(user);
                isFirstSignIn = true;
                // The claims transformation may have cached the default Viewer role for
                // this email before the row existed — evict so the real role applies now.
                cache.Remove(RoleClaimsTransformation.RoleCacheKey(email));
            }
            else
            {
                // Treat a gap of > 1h since the last request as a new sign-in session so
                // the audit trail covers sign-ins without logging every page load.
                isNewSession = now - user.LastSeenAt > TimeSpan.FromHours(1);
                user.LastSeenAt = now;
                if (!string.IsNullOrEmpty(displayName)) user.DisplayName = displayName;
            }
            await db.SaveChangesAsync(ct);

            if (isFirstSignIn)
                await audit.WriteAsync("auth.first_signin", "user", email, $"first sign-in, role {user.Role}", ct);
            else if (isNewSession)
                await audit.WriteAsync("auth.signin", "user", email, $"signed in as {user.Role}", ct);

            return Results.Ok(new { name = user.DisplayName ?? "", email = user.Email, role = user.Role });
        });
    }
}
