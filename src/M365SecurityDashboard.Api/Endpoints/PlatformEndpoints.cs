using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>
/// Collection control, the tenant audit-event feed, and entity investigation —
/// the platform-level surfaces that sit underneath the dashboards rather than
/// belonging to any one of them.
/// </summary>
public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this WebApplication app)
    {
        // ── Collection runs ─────────────────────────────────────────────────
        app.MapGet("/api/collector/runs", async (AppDbContext db, CancellationToken ct) =>
            await db.CollectionRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(20).ToListAsync(ct));

        app.MapPost("/api/collector/run", async (
            IServiceProvider services,
            Microsoft.Extensions.Options.IOptions<GraphOptions> options,
            CancellationToken ct) =>
        {
            if (!options.Value.IsConfigured())
                return Results.BadRequest(new { error = "Microsoft Graph is not configured. Complete the setup wizard first." });

            var collector = services.GetRequiredService<GraphCollector>();
            try
            {
                var run = await collector.CollectAsync(ct);
                return Results.Ok(run);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress"))
            {
                return Results.Conflict(new { error = "A collection run is already in progress." });
            }
        }).RequireAuthorization("RequireAnalyst");

        // ── Tenant audit events (activity feed backing activity-based policies) ─────
        app.MapGet("/api/audit-events", async (
            AppDbContext db, string? search, string? activity, string? source, int days = 7,
            int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            page = page < 1 ? 1 : page;
            pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90));

            var q = db.AuditEvents.AsNoTracking().Where(e => e.OccurredAt >= since);
            // "entra" = the directory audit; a product name ("Power Automate") = that
            // product's Purview audit events. Anything else is ignored, not an error.
            if (string.Equals(source, "entra", StringComparison.OrdinalIgnoreCase))
                q = q.Where(e => e.Source == "directoryAudit");
            else if (PurviewAuditNormalizer.Products.FirstOrDefault(p => p.Equals(source, StringComparison.OrdinalIgnoreCase)) is { } product)
                q = q.Where(e => e.Source == PurviewAuditNormalizer.Source && e.Category == product);
            if (!string.IsNullOrWhiteSpace(activity))
            {
                var safePattern = activity
                    .Replace("[", "[[]")
                    .Replace("%", "[%]")
                    .Replace("_", "[_]")
                    .Replace("*", "%");
                q = q.Where(e => EF.Functions.Like(e.Activity, safePattern));
            }
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(e =>
                    e.Activity.Contains(search) ||
                    (e.ActorUpn != null && e.ActorUpn.Contains(search)) ||
                    (e.TargetName != null && e.TargetName.Contains(search)));

            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(e => e.OccurredAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(e => new { e.Id, e.Activity, e.Category, e.Source, e.ActorUpn, e.ActorApp, e.TargetName, e.Result, e.OccurredAt })
                .ToListAsync(ct);
            return Results.Ok(new { total, page, pageSize, items });
        });

        // ── Purview audit status (Copilot agents, Power Platform) ──────────────────
        // What the Rules page needs to say "needs permission" or when it last ran.
        // The high-risk connector list is install configuration, shown read-only.
        app.MapGet("/api/purview-audit/status", async (
            AppDbContext db, Microsoft.Extensions.Options.IOptions<GraphOptions> options, CancellationToken ct) =>
        {
            var states = await db.PurviewAuditStates.AsNoTracking().ToListAsync(ct);
            var feeds = PurviewAuditCollector.Feeds.Select(f => states.FirstOrDefault(s => s.Feed == f) is { } s
                ? new { feed = f, status = s.Status, s.LastRunAt, s.LastSuccessAt, s.LastEventCount, s.LastError, pending = s.PendingQueryId != null }
                : new { feed = f, status = "NotStarted", LastRunAt = (DateTimeOffset?)null, LastSuccessAt = (DateTimeOffset?)null, LastEventCount = 0, LastError = (string?)null, pending = false });
            var o = options.Value;
            return Results.Ok(new
            {
                enabled = o.PurviewAuditIntervalMinutes > 0,
                intervalMinutes = o.PurviewAuditIntervalMinutes,
                highRiskConnectors = o.PowerPlatformHighRiskConnectors ?? [],
                feeds,
            });
        });

        // ── Entity investigation profile (drill-down) ──────────────────────────────
        // GET /api/entity/{kind}/{id} — kind = user|device. Merges the entity's alerts
        // and tenant audit activity into one reverse-chronological timeline.
        app.MapGet("/api/entity/{kind}/{id}", async (EntityProfileBuilder builder, string kind, string id, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new { error = "Entity id is required." });
            var profile = await builder.BuildAsync(kind, id, maxItems: 300, ct);
            return Results.Ok(profile);
        }).RequireAuthorization("RequireAnalyst");
    }
}
