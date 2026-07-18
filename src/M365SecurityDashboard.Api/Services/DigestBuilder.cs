using System.Globalization;
using System.Net;
using System.Text;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Assembles the executive security digest — current posture, week-over-week
/// trend movement, and the top open alerts — into an HTML email body and an
/// optional CSV summary. Reads only local collected data; never calls Graph.
/// </summary>
public sealed class DigestBuilder(AppDbContext db)
{
    public sealed record Metric(string Label, string Value, double? Delta, string DeltaLabel, bool HigherIsWorse);

    public sealed record TopAlert(string PolicyName, string Severity, string Condition, int MetricValue, DateTimeOffset TriggeredAt);

    public sealed record Digest(
        string Subject,
        string HtmlBody,
        string? Csv,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<Metric> Metrics,
        IReadOnlyList<TopAlert> TopAlerts,
        bool HasData);

    /// <summary>Builds the digest for the trailing <paramref name="windowDays"/> (default 7).</summary>
    public async Task<Digest> BuildAsync(int windowDays, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddDays(-Math.Max(1, windowDays));

        // Posture "now" = the most recent snapshot; "prior" = the last snapshot at or
        // before the window start, so deltas reflect movement across the period.
        var latest = await db.TrendSnapshots.AsNoTracking()
            .OrderByDescending(t => t.CapturedAt).FirstOrDefaultAsync(ct);
        var prior = await db.TrendSnapshots.AsNoTracking()
            .Where(t => t.CapturedAt <= windowStart)
            .OrderByDescending(t => t.CapturedAt).FirstOrDefaultAsync(ct);

        var metrics = BuildMetrics(latest, prior);

        // Open alerts, excluding those currently snoozed — a snoozed alert has been
        // deliberately silenced and shouldn't surface in an executive summary as if
        // it were unhandled.
        var openAlerts = (await db.TriggeredAlerts.AsNoTracking()
                .Where(t => t.Status == "new" || t.Status == "acknowledged")
                .ToListAsync(ct))
            .Where(t => t.SnoozedUntil == null || t.SnoozedUntil <= now)
            .ToList();
        // Top open alerts by severity then recency, capped so the email stays scannable.
        var topAlerts = openAlerts
            .OrderByDescending(a => SeverityRank(a.Severity))
            .ThenByDescending(a => a.TriggeredAt)
            .Take(10)
            .Select(a => new TopAlert(a.PolicyName, a.Severity, a.Condition, a.MetricValue, a.TriggeredAt))
            .ToList();

        var newInWindow = openAlerts.Count(a => a.TriggeredAt >= windowStart);
        var hasData = latest != null || openAlerts.Count > 0;

        var subject = $"[Vigil365] Weekly security digest — {now:yyyy-MM-dd}";
        var html = RenderHtml(now, windowDays, metrics, topAlerts, openAlerts.Count, newInWindow, latest == null);
        var csv = RenderCsv(now, metrics, topAlerts);

        return new Digest(subject, html, csv, now, metrics, topAlerts, hasData);
    }

    private static List<Metric> BuildMetrics(TrendSnapshot? latest, TrendSnapshot? prior)
    {
        if (latest == null) return [];
        double? D(Func<TrendSnapshot, double> sel) => prior == null ? null : sel(latest) - sel(prior);
        static string Pct(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + "%";

        return
        [
            new("Secure Score", Pct(latest.SecureScorePct), D(t => t.SecureScorePct), "pts", HigherIsWorse: false),
            new("MFA coverage", Pct(latest.MfaCoveragePct), D(t => t.MfaCoveragePct), "pts", HigherIsWorse: false),
            new("Risky users", latest.RiskyUsersCount.ToString(), D(t => t.RiskyUsersCount), "", HigherIsWorse: true),
            new("Non-compliant devices", latest.NonCompliantDevicesCount.ToString(), D(t => t.NonCompliantDevicesCount), "", HigherIsWorse: true),
            new("Critical alerts", latest.CriticalAlertsCount.ToString(), D(t => t.CriticalAlertsCount), "", HigherIsWorse: true),
            new("High alerts", latest.HighAlertsCount.ToString(), D(t => t.HighAlertsCount), "", HigherIsWorse: true),
            new("Compliance issues", latest.ComplianceIssuesCount.ToString(), D(t => t.ComplianceIssuesCount), "", HigherIsWorse: true),
        ];
    }

    private static int SeverityRank(string? sev) => (sev ?? "").ToLowerInvariant() switch
    {
        "critical" => 4, "high" => 3, "medium" => 2, "low" => 1, _ => 0,
    };

    private static string SevColor(string? sev) => (sev ?? "").ToLowerInvariant() switch
    {
        "critical" => "dc2626", "high" => "ea580c", "medium" => "d97706", "low" => "2563eb", _ => "6b7280",
    };

    /// <summary>Delta rendered as a colored ▲/▼ chip — green when moving the safe way.</summary>
    private static string DeltaChip(Metric m)
    {
        if (m.Delta is not { } d || Math.Abs(d) < 0.05)
            return "<span style=\"color:#94a3b8\">—</span>";
        var worse = m.HigherIsWorse ? d > 0 : d < 0;
        var color = worse ? "#dc2626" : "#16a34a";
        var arrow = d > 0 ? "▲" : "▼";
        var mag = Math.Abs(d).ToString("0.#", CultureInfo.InvariantCulture);
        var suffix = string.IsNullOrEmpty(m.DeltaLabel) ? "" : " " + m.DeltaLabel;
        return $"<span style=\"color:{color}\">{arrow} {mag}{suffix}</span>";
    }

    private static string RenderHtml(
        DateTimeOffset now, int windowDays, IReadOnlyList<Metric> metrics,
        IReadOnlyList<TopAlert> topAlerts, int openCount, int newInWindow, bool noPosture)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:640px;color:#0f172a\">");
        sb.Append("<h2 style=\"color:#2563eb;margin:0 0 4px\">Vigil365 — Weekly Security Digest</h2>");
        sb.Append($"<p style=\"margin:0 0 16px;color:#64748b;font-size:13px\">Posture as of {now:dddd, dd MMM yyyy HH:mm} UTC · trailing {windowDays} days</p>");

        sb.Append($"<div style=\"background:#f1f5f9;border-radius:10px;padding:12px 16px;margin:0 0 18px;font-size:14px\">"
            + $"<b>{openCount}</b> open alert{(openCount == 1 ? "" : "s")} · <b>{newInWindow}</b> new this period</div>");

        if (noPosture)
        {
            sb.Append("<p style=\"color:#64748b;font-size:14px\">No posture snapshots have been captured yet. "
                + "Trend metrics will appear once the collector has run at least once.</p>");
        }
        else
        {
            sb.Append("<table style=\"border-collapse:collapse;width:100%;font-size:14px;margin:0 0 20px\">");
            sb.Append("<tr style=\"text-align:left;color:#64748b;font-size:12px\">"
                + "<th style=\"padding:6px 8px;border-bottom:1px solid #e2e8f0\">Metric</th>"
                + "<th style=\"padding:6px 8px;border-bottom:1px solid #e2e8f0\">Current</th>"
                + "<th style=\"padding:6px 8px;border-bottom:1px solid #e2e8f0\">Change</th></tr>");
            foreach (var m in metrics)
            {
                sb.Append("<tr>"
                    + $"<td style=\"padding:6px 8px;border-bottom:1px solid #f1f5f9\">{WebUtility.HtmlEncode(m.Label)}</td>"
                    + $"<td style=\"padding:6px 8px;border-bottom:1px solid #f1f5f9\"><b>{WebUtility.HtmlEncode(m.Value)}</b></td>"
                    + $"<td style=\"padding:6px 8px;border-bottom:1px solid #f1f5f9\">{DeltaChip(m)}</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append("<h3 style=\"margin:0 0 8px;font-size:15px\">Top open alerts</h3>");
        if (topAlerts.Count == 0)
        {
            sb.Append("<p style=\"color:#16a34a;font-size:14px;margin:0 0 16px\">No open alerts. 🎉</p>");
        }
        else
        {
            sb.Append("<table style=\"border-collapse:collapse;width:100%;font-size:13px;margin:0 0 20px\">");
            foreach (var a in topAlerts)
            {
                sb.Append("<tr>"
                    + $"<td style=\"padding:6px 8px;border-bottom:1px solid #f1f5f9;white-space:nowrap\">"
                    + $"<span style=\"display:inline-block;padding:1px 8px;border-radius:10px;color:#fff;font-size:11px;background:#{SevColor(a.Severity)}\">{WebUtility.HtmlEncode(a.Severity.ToUpperInvariant())}</span></td>"
                    + $"<td style=\"padding:6px 8px;border-bottom:1px solid #f1f5f9\"><b>{WebUtility.HtmlEncode(a.PolicyName)}</b><br>"
                    + $"<span style=\"color:#64748b\">{WebUtility.HtmlEncode(a.Condition)}</span></td>"
                    + $"<td style=\"padding:6px 8px;border-bottom:1px solid #f1f5f9;color:#64748b;white-space:nowrap\">{a.TriggeredAt:dd MMM}</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append("<p style=\"color:#94a3b8;font-size:12px;margin:12px 0 0\">Generated by Vigil365 · read-only monitoring · no changes were made to your tenant.</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string RenderCsv(DateTimeOffset now, IReadOnlyList<Metric> metrics, IReadOnlyList<TopAlert> topAlerts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Vigil365 Weekly Security Digest,{now:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine("Metric,Current,Change");
        foreach (var m in metrics)
        {
            var delta = m.Delta is { } d ? d.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(m.DeltaLabel) ? "" : " " + m.DeltaLabel) : "n/a";
            sb.AppendLine($"{Csv(m.Label)},{Csv(m.Value)},{Csv(delta)}");
        }
        sb.AppendLine();
        sb.AppendLine("Severity,Policy,Condition,Value,Triggered (UTC)");
        foreach (var a in topAlerts)
            sb.AppendLine($"{Csv(a.Severity)},{Csv(a.PolicyName)},{Csv(a.Condition)},{a.MetricValue},{a.TriggeredAt:yyyy-MM-dd HH:mm}");
        return sb.ToString();
    }

    /// <summary>RFC-4180 field escaping.</summary>
    private static string Csv(string? s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }
}
