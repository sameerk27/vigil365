using M365SecurityDashboard.Api.Services;
using M365SecurityDashboard.Api.Services.PowerPlatform;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>
/// Power Platform inventory (docs/COPILOT_POWER_PLATFORM_PLAN.md, phase D): an option
/// each tenant turns on by giving Vigil365's app the read-only Power Platform reader
/// role. Until then the endpoint answers NotEnabled with what to run.
/// </summary>
public static class PowerPlatformEndpoints
{
    /// <summary>Microsoft's built-in "Power Platform reader" role: read-only access to all resources.</summary>
    public const string ReaderRoleId = "c886ad2e-27f7-4874-8381-5849b8d8a090";
    public const string GrantScript = "grant-power-platform-reader.ps1";

    public static void MapPowerPlatformEndpoints(this WebApplication app)
    {
        app.MapGet("/api/power-platform/inventory", async (
            bool? refresh, TenantGraphCredentials creds, PowerPlatformInventoryService inventory, CancellationToken ct) =>
        {
            var o = await creds.ResolveAsync(ct);
            if (!o.IsConfigured()) return Results.Ok(new { configured = false });

            var r = await inventory.GetAsync(refresh == true, ct);
            return Results.Ok(new
            {
                configured = true,
                status = r.Status,
                reason = r.Reason,
                readAt = r.ReadAt,
                summary = r.Summary,
                environments = r.Environments,
                apps = r.Apps,
                flows = r.Flows,
                setup = new
                {
                    tenantId = o.TenantId,
                    appId = o.ClientId,
                    role = "Power Platform reader",
                    roleId = ReaderRoleId,
                    script = GrantScript,
                    command = $".\\{GrantScript} -TenantId {o.TenantId} -AppId {o.ClientId}",
                },
            });
        });
    }
}
