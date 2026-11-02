using System.Text;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Services;
using M365SecurityDashboard.Api.Services.Posture;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>
/// Licence posture (docs/LICENSE_POSTURE_PLAN.md): what the current tenant's licences
/// allow, what is set up, what is paid for but unused, and what would need another
/// licence. Evaluated live from Graph on each request; read-only.
/// </summary>
public static class PostureEndpoints
{
    public static void MapPostureEndpoints(this WebApplication app)
    {
        app.MapGet("/api/posture", async (TenantGraphCredentials creds, PostureCollector collector, CancellationToken ct) =>
        {
            if (!await creds.IsConfiguredAsync(ct)) return Results.Ok(new { configured = false });
            var catalog = PostureCatalog.Default;
            var result = PostureEvaluator.Evaluate(await collector.CollectAsync(catalog, ct), catalog);
            return Results.Ok(ToDto(result, catalog));
        });

        // A self-contained HTML file for the client. White-label: the client's brand
        // name and colour when the MSP set them, else Vigil365's.
        app.MapGet("/api/posture/report.html", async (TenantGraphCredentials creds, PostureCollector collector, AppDbContext db, CancellationToken ct) =>
        {
            if (!await creds.IsConfiguredAsync(ct)) return Results.BadRequest(new { error = "Connect Microsoft Graph in Setup first." });
            var catalog = PostureCatalog.Default;
            var result = PostureEvaluator.Evaluate(await collector.CollectAsync(catalog, ct), catalog);
            var tenant = db.CurrentTenantIdOrNull is Guid id
                ? await db.ClientTenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
                : null;
            var client = result.OrganizationName ?? tenant?.Name ?? "Microsoft 365 tenant";
            var html = PostureReport.Html(result, client, tenant?.BrandName ?? "Vigil365", tenant?.BrandAccentColor, DateTimeOffset.UtcNow);
            var file = $"licence-posture-{Slug(client)}-{DateTimeOffset.UtcNow:yyyy-MM-dd}.html";
            return Results.File(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", file);
        });
    }

    private static object ToDto(PostureResult r, PostureCatalog catalog)
    {
        var graphUnknown = r.Findings.Count(f => f.Status == PostureStatus.Unknown && f.ReadFrom == "Microsoft Graph");
        return new
        {
            configured = true,
            generatedAt = r.GeneratedAt,
            organizationName = r.OrganizationName,
            error = r.Error,
            licences = r.Licences.Select(l => new { l.PartNumber, l.Name, l.Enabled, l.Consumed }),
            includedFeatures = r.IncludedFeatures,
            summary = r.Summary,
            scuba = new { pass = r.Scuba.Pass, total = r.Scuba.Total },
            secureScore = r.SecureScore is { } s && s.Max > 0 ? new { current = s.Current, max = s.Max } : null,
            entitlementSource = catalog.EntitlementSource,
            sources = new object[]
            {
                new { name = "Microsoft Graph", state = r.Error is not null ? "Couldn't read" : graphUnknown > 0 ? "Partly read" : "Connected",
                      detail = r.Error ?? (graphUnknown > 0 ? $"{graphUnknown} check{(graphUnknown == 1 ? "" : "s")} couldn't be read" : "All Graph checks read") },
                new { name = "Exchange Online PowerShell", state = "Not set up yet",
                      detail = "Safe Links and preset policies are read here. Coming in the next phase." },
            },
            findings = r.Findings.Select(f => new
            {
                id = f.Check.Id,
                title = f.Check.Title,
                workload = f.Check.Workload,
                status = f.Status.ToString(),
                detail = f.Detail,
                licensedBy = f.LicensedBy,
                unlockedBy = f.UnlockedBy,
                readFrom = f.ReadFrom,
                whyItMatters = f.Check.WhyItMatters,
                minimum = f.Check.Minimum,
                fix = f.Check.Fix,
                portalUrl = f.Check.PortalUrl,
                feature = catalog.Feature(f.Check.Feature).Name,
                featureSource = catalog.Feature(f.Check.Feature).Source,
                baselines = f.Check.Baselines,
            }),
            upgrades = r.Upgrades.Select(g => new { unlockedBy = g.UnlockedBy, checks = g.Findings.Select(f => f.Check.Id) }),
        };
    }

    private static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "tenant" : slug.Length > 40 ? slug[..40] : slug;
    }
}
