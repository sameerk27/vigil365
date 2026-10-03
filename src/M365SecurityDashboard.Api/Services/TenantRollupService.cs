using System.Net;
using System.Text;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// The cross-tenant rollup: per client, open triggered alerts by severity,
/// unresolved critical/high security alerts, and collection health. Serves the
/// Clients page and the MSP digest email. The only reads here go through
/// <see cref="AppDbContext.CrossTenant{T}"/> — this is, by design, a place that
/// sees every permitted tenant at once.
/// </summary>
public sealed class TenantRollupService(AppDbContext db, TenantGraphCredentials creds)
{
    public sealed record Open(int Critical, int High, int Medium, int Low)
    {
        public int Total => Critical + High + Medium + Low;
    }

    public sealed record Row(
        Guid Id, string Name, bool Configured,
        DateTimeOffset? LastCollectionAt, string? LastCollectionStatus, string? LastError,
        Open OpenAlerts, int UnresolvedCritical, int UnresolvedHigh, string Health);

    public async Task<IReadOnlyList<Row>> BuildAsync(IReadOnlyList<ClientTenant> tenants, CancellationToken ct)
    {
        var ids = tenants.Select(t => t.Id).ToList();
        if (ids.Count == 0) return [];

        var open = await db.CrossTenant<TriggeredAlert>().AsNoTracking()
            .Where(t => ids.Contains(t.TenantId) && (t.Status == "new" || t.Status == "acknowledged"))
            .GroupBy(t => new { t.TenantId, t.Severity })
            .Select(g => new { g.Key.TenantId, g.Key.Severity, Count = g.Count() })
            .ToListAsync(ct);
        var unresolved = await db.CrossTenant<SecurityAlert>().AsNoTracking()
            .Where(a => ids.Contains(a.TenantId) && !a.IsResolved && (a.Severity == AlertSeverity.Critical || a.Severity == AlertSeverity.High))
            .GroupBy(a => new { a.TenantId, a.Severity })
            .Select(g => new { g.Key.TenantId, g.Key.Severity, Count = g.Count() })
            .ToListAsync(ct);

        int OpenCount(Guid id, params string[] sevs) => open
            .Where(o => o.TenantId == id && sevs.Any(s => string.Equals(o.Severity, s, StringComparison.OrdinalIgnoreCase)))
            .Sum(o => o.Count);
        int Unresolved(Guid id, AlertSeverity sev) => unresolved.Where(u => u.TenantId == id && u.Severity == sev).Sum(u => u.Count);

        return tenants.Select(t =>
        {
            var configured = creds.Resolve(t).IsConfigured();
            var o = new Open(OpenCount(t.Id, "critical"), OpenCount(t.Id, "high"), OpenCount(t.Id, "medium"), OpenCount(t.Id, "low", "informational"));
            var health = HealthOf(configured, t, o);
            return new Row(t.Id, t.Name, configured, t.LastCollectionAt, t.LastCollectionStatus, t.LastError,
                o, Unresolved(t.Id, AlertSeverity.Critical), Unresolved(t.Id, AlertSeverity.High), health);
        }).OrderByDescending(r => Rank(r)).ThenByDescending(r => r.OpenAlerts.Critical).ThenByDescending(r => r.OpenAlerts.High).ThenBy(r => r.Name).ToList();
    }

    /// <summary>One open alert in the cross-client queue (MSP_V12_PLAN.md U6).</summary>
    public sealed record QueueItem(
        Guid Id, Guid TenantId, string TenantName, string PolicyName, string Severity, string Category,
        string Condition, int MetricValue, DateTimeOffset TriggeredAt, string Status, string? AssignedTo, DateTimeOffset? SnoozedUntil);

    /// <summary>
    /// Open (new/acknowledged) triggered alerts across the given clients, most
    /// severe first, then newest. The MSP's single triage list — deliberately
    /// cross-tenant, and only ever over tenants the caller is permitted to see.
    /// </summary>
    public async Task<IReadOnlyList<QueueItem>> OpenAlertsAcrossAsync(IReadOnlyList<ClientTenant> tenants, int limit, CancellationToken ct)
    {
        var names = tenants.ToDictionary(t => t.Id, t => t.Name);
        if (names.Count == 0) return [];
        var ids = names.Keys.ToList();
        // Ranked by severity in the database before the limit, so an older
        // critical alert is never cut in favour of newer low ones.
        var rows = await db.CrossTenant<TriggeredAlert>().AsNoTracking()
            .Where(t => ids.Contains(t.TenantId) && (t.Status == "new" || t.Status == "acknowledged"))
            .OrderByDescending(t => t.Severity.ToLower() == "critical" ? 4
                : t.Severity.ToLower() == "high" ? 3
                : t.Severity.ToLower() == "medium" ? 2
                : t.Severity.ToLower() == "low" ? 1 : 0)
            .ThenByDescending(t => t.TriggeredAt)
            .Take(Math.Clamp(limit, 1, 2000))
            .ToListAsync(ct);
        return rows
            .Select(t => new QueueItem(t.Id, t.TenantId, names[t.TenantId], t.PolicyName, t.Severity, t.Category,
                t.Condition, t.MetricValue, t.TriggeredAt, t.Status, t.AssignedTo, t.SnoozedUntil))
            .ToList();
    }

    public static string HealthOf(bool configured, ClientTenant t, Open o) =>
        !configured ? "neutral"
        : t.LastCollectionStatus == "Failed" ? "error"
        : o.Critical > 0 || t.LastError is not null ? "error"
        : o.High > 0 ? "warning"
        : t.LastCollectionAt is null ? "neutral" : "good";

    private static int Rank(Row r) => r.Health switch { "error" => 3, "warning" => 2, "neutral" => 1, _ => 0 };

    // ── MSP digest ────────────────────────────────────────────────────────────

    /// <summary>One MSP digest per calendar day, at the configured UTC hour.</summary>
    public static bool IsMspDigestDue(NotificationSettings install, DateTimeOffset now)
    {
        if (!install.MspDigestEnabled) return false;
        if (now.Hour != Math.Clamp(install.MspDigestHourUtc, 0, 23)) return false;
        if (install.LastMspDigestAt is { } last && last.UtcDateTime.Date == now.UtcDateTime.Date) return false;
        return true;
    }

    public static string MspDigestSubject(IReadOnlyList<Row> rows)
    {
        var crit = rows.Sum(r => r.OpenAlerts.Critical);
        var high = rows.Sum(r => r.OpenAlerts.High);
        var down = rows.Count(r => r.Health == "error" && r.LastCollectionStatus == "Failed");
        return $"Vigil365 MSP digest — {rows.Count} client{(rows.Count == 1 ? "" : "s")}: {crit} critical, {high} high open" + (down > 0 ? $", {down} not collecting" : "");
    }

    /// <summary>Plain, mail-client-safe HTML: one row per client, worst first.</summary>
    public static string MspDigestHtml(IReadOnlyList<Row> rows, DateTimeOffset now, string? appUrl)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#111\">");
        sb.Append($"<h2 style=\"margin:0 0 4px\">Vigil365 MSP digest</h2><div style=\"color:#555;margin-bottom:12px\">{now:yyyy-MM-dd HH:mm} UTC · {rows.Count} client{(rows.Count == 1 ? "" : "s")}, worst first</div>");
        sb.Append("<table cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse;width:100%\"><thead><tr style=\"background:#f3f4f6\">");
        foreach (var h in new[] { "Client", "Health", "Critical", "High", "Medium", "Low", "Unresolved crit/high", "Last collection" })
            sb.Append($"<th align=\"left\" style=\"border-bottom:1px solid #ddd\">{h}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var r in rows)
        {
            var color = r.Health switch { "error" => "#dc2626", "warning" => "#d97706", "good" => "#16a34a", _ => "#6b7280" };
            var status = !r.Configured ? "Not connected" : r.LastCollectionStatus ?? "Pending";
            var last = r.LastCollectionAt is { } at ? at.ToString("yyyy-MM-dd HH:mm") + " UTC" : "never";
            sb.Append("<tr>");
            sb.Append($"<td style=\"border-bottom:1px solid #eee\"><strong>{WebUtility.HtmlEncode(r.Name)}</strong></td>");
            sb.Append($"<td style=\"border-bottom:1px solid #eee;color:{color}\">{WebUtility.HtmlEncode(status)}</td>");
            sb.Append($"<td style=\"border-bottom:1px solid #eee;{(r.OpenAlerts.Critical > 0 ? "color:#dc2626;font-weight:600" : "")}\">{r.OpenAlerts.Critical}</td>");
            sb.Append($"<td style=\"border-bottom:1px solid #eee;{(r.OpenAlerts.High > 0 ? "color:#d97706;font-weight:600" : "")}\">{r.OpenAlerts.High}</td>");
            sb.Append($"<td style=\"border-bottom:1px solid #eee\">{r.OpenAlerts.Medium}</td><td style=\"border-bottom:1px solid #eee\">{r.OpenAlerts.Low}</td>");
            sb.Append($"<td style=\"border-bottom:1px solid #eee\">{r.UnresolvedCritical} / {r.UnresolvedHigh}</td>");
            sb.Append($"<td style=\"border-bottom:1px solid #eee\">{last}{(r.LastError is not null ? $"<br><span style=\"color:#dc2626\">{WebUtility.HtmlEncode(Truncate(r.LastError, 120))}</span>" : "")}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        if (!string.IsNullOrWhiteSpace(appUrl))
            sb.Append($"<p style=\"margin-top:12px\"><a href=\"{WebUtility.HtmlEncode(appUrl.TrimEnd('/'))}/#/clients\">Open the Clients view</a></p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
