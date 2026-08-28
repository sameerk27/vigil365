using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

/// <summary>One metric's movement from the captured baseline to the latest snapshot.</summary>
public sealed record DriftRow(
    string Metric,
    string Baseline,   // formatted for display (mono)
    string Current,
    string Drift,      // signed, e.g. "-2.1 pts" / "+1" / "0"
    string Tone,       // "good" (held/improved) | "warn" (regressed) | "neutral" (moved, not bad)
    double Delta);     // raw current - baseline (sign kept)

/// <summary>
/// Pure computation of baseline drift. No fabricated values — every number
/// comes from the captured <see cref="TenantBaseline"/> and the latest
/// <see cref="TrendSnapshot"/> the collector produced.
/// </summary>
public static class BaselineDrift
{
    private sealed record MetricDef(string Label, string Unit, bool IsPct, bool LowerIsBetter,
        Func<TenantBaseline, double> FromBaseline, Func<TrendSnapshot, double> FromSnapshot);

    private static readonly MetricDef[] Metrics =
    [
        new("Secure Score",          "pts", true,  false, b => b.SecureScorePct,          s => s.SecureScorePct),
        new("MFA coverage",          "pts", true,  false, b => b.MfaCoveragePct,          s => s.MfaCoveragePct),
        new("Risky users",           "",    false, true,  b => b.RiskyUsersCount,         s => s.RiskyUsersCount),
        new("Non-compliant devices", "",    false, true,  b => b.NonCompliantDevicesCount, s => s.NonCompliantDevicesCount),
        new("Critical alerts",       "",    false, true,  b => b.CriticalAlertsCount,     s => s.CriticalAlertsCount),
        new("High alerts",           "",    false, true,  b => b.HighAlertsCount,         s => s.HighAlertsCount),
        new("Compliance issues",     "",    false, true,  b => b.ComplianceIssuesCount,   s => s.ComplianceIssuesCount),
    ];

    private static string Fmt(double v, bool isPct) => isPct ? $"{v:0.0}%" : $"{v:0}";

    public static IReadOnlyList<DriftRow> Compute(TenantBaseline baseline, TrendSnapshot? latest)
    {
        var rows = new List<DriftRow>(Metrics.Length);
        foreach (var m in Metrics)
        {
            double b = m.FromBaseline(baseline);
            double c = latest is null ? b : m.FromSnapshot(latest);
            double delta = c - b;

            string driftText;
            if (Math.Abs(delta) < (m.IsPct ? 0.05 : 0.5))
                driftText = "0";
            else
            {
                string sign = delta > 0 ? "+" : "−"; // real minus sign
                double mag = Math.Abs(delta);
                driftText = m.IsPct ? $"{sign}{mag:0.0} {m.Unit}" : $"{sign}{mag:0}";
            }

            // Tone: held == good; moved in the better direction == good;
            // moved in the worse direction == warn.
            string tone;
            if (Math.Abs(delta) < (m.IsPct ? 0.05 : 0.5)) tone = "good";
            else
            {
                bool improved = m.LowerIsBetter ? delta < 0 : delta > 0;
                tone = improved ? "good" : "warn";
            }

            rows.Add(new DriftRow(m.Label, Fmt(b, m.IsPct), Fmt(c, m.IsPct), driftText, tone, delta));
        }
        return rows;
    }

    /// <summary>Copy the newest snapshot's metrics into a baseline row.</summary>
    public static void CaptureFrom(TenantBaseline target, TrendSnapshot snapshot, string capturedBy, DateTimeOffset now)
    {
        target.CapturedAt = now;
        target.CapturedBy = capturedBy;
        target.RiskyUsersCount = snapshot.RiskyUsersCount;
        target.MfaCoveragePct = snapshot.MfaCoveragePct;
        target.NonCompliantDevicesCount = snapshot.NonCompliantDevicesCount;
        target.CriticalAlertsCount = snapshot.CriticalAlertsCount;
        target.HighAlertsCount = snapshot.HighAlertsCount;
        target.SecureScorePct = snapshot.SecureScorePct;
        target.ComplianceIssuesCount = snapshot.ComplianceIssuesCount;
    }
}
