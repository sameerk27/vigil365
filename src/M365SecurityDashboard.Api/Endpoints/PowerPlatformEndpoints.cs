using M365SecurityDashboard.Api.Services;
using M365SecurityDashboard.Api.Services.PowerPlatform;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>
/// Power Platform inventory (docs/COPILOT_POWER_PLATFORM_PLAN.md, phase D). The browser
/// reads Microsoft's Power Platform inventory with the signed-in person's own Microsoft
/// sign-in; Microsoft refuses apps ("Caller is not an authorized app") unless they are
/// enrolled with Power Platform Administrator rights, which Vigil365 doesn't take. This
/// server only evaluates what the browser read and names owners through Graph. It never
/// calls the Power Platform API and never sees the person's token.
/// </summary>
public static class PowerPlatformEndpoints
{
    /// <summary>Rows accepted per evaluation: well above a large tenant, small enough to bound the work.</summary>
    public const int MaxResources = 200_000;

    /// <summary>
    /// Bytes accepted per evaluation. Kestrel's 30 MB default turned a large tenant's read
    /// (tens of thousands of rows plus app detail) into a 413 well before MaxResources.
    /// </summary>
    public const long MaxRequestBytes = 128L * 1024 * 1024;

    public static void MapPowerPlatformEndpoints(this WebApplication app)
    {
        // What the browser needs to read: which tenant to sign in to, and where.
        app.MapGet("/api/power-platform/settings", async (TenantGraphCredentials creds, CancellationToken ct) =>
        {
            var o = await creds.ResolveAsync(ct);
            if (!o.IsConfigured()) return Results.Ok(new { configured = false });
            return Results.Ok(new
            {
                configured = true,
                tenantId = o.TenantId,
                powerPlatformApi = creds.Global.PowerPlatformApiBaseUrl.TrimEnd('/'),
                // The install-wide Graph host: it's the one the Content Security Policy allows
                // (Program.cs), and the browser signs in through the install-wide authority anyway.
                graphApi = creds.Global.BaseUrl.TrimEnd('/'),
                maxEnvironments = creds.Global.PowerPlatformMaxEnvironments,
            });
        });

        app.MapPost("/api/power-platform/inventory/evaluate", async (
            PowerPlatformEvaluateRequest request, TenantGraphCredentials creds, PowerPlatformOwners owners, CancellationToken ct) =>
        {
            if (request.Resources.Count > MaxResources || request.AppDetails.Sum(d => d.Items.Count) > MaxResources)
                return Results.BadRequest(new { error = $"More than {MaxResources} items; Vigil365 doesn't evaluate inventories this large." });

            var o = await creds.ResolveAsync(ct);
            var ownerIds = PowerPlatformEvaluator.OwnerIds(request);
            var resolved = o.IsConfigured() ? await owners.ResolveAsync(ownerIds, ct) : null;
            var result = PowerPlatformEvaluator.Evaluate(request, creds.Global, resolved, DateTimeOffset.UtcNow);
            if (resolved is null) result = result with { Notes = [.. result.Notes, "Owner names couldn't be read from Microsoft Graph, so owners show as ids and owners who left aren't flagged."] };
            else if (ownerIds.Count > PowerPlatformOwners.MaxOwners)
                result = result with { Notes = [.. result.Notes, $"Owners were looked up for the first {PowerPlatformOwners.MaxOwners} of {ownerIds.Count} people; the rest show as ids and aren't checked for having left."] };
            return Results.Ok(result);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBytes));
    }
}
