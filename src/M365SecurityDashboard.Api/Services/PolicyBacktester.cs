using Microsoft.EntityFrameworkCore;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Answers "if this policy had been enabled, how often would it have fired?"
/// against stored history, before an analyst commits to a threshold.
///
/// Honesty rule: only claim a result where real history exists. Activity
/// policies replay exactly (audit events are timestamped). Metric policies can
/// only be replayed where a trend-snapshot field records that metric over time.
/// Anything else returns Supported=false with the reason — reporting "0 times"
/// for something we cannot measure would make an untested policy look safe.
/// </summary>
public sealed class PolicyBacktester(AppDbContext db)
{
    public sealed record Result(
        bool Supported,
        string? UnsupportedReason,
        int WindowDays,
        int Threshold,
        int WouldFireCount,
        int MaxObservedValue,
        int SamplesEvaluated,
        string Basis,
        IReadOnlyList<DateTimeOffset> FiredAt);

    /// <summary>Metric names that a TrendSnapshot actually records over time.</summary>
    private static readonly Dictionary<string, Func<TrendSnapshot, int>> SnapshotMetrics =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["riskyUsersCount"]    = s => s.RiskyUsersCount,
            ["nonCompliantCount"]  = s => s.NonCompliantDevicesCount,
            ["criticalAlertCount"] = s => s.CriticalAlertsCount,
            ["highAlertCount"]     = s => s.HighAlertsCount,
        };

    public async Task<Result> RunAsync(AlertPolicy policy, int days, TimeSpan evalInterval, CancellationToken ct)
    {
        var windowDays = Math.Clamp(days, 1, 90);
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-windowDays);
        var threshold = Math.Max(1, policy.Threshold);

        if (policy.Kind.Equals("anomaly", StringComparison.OrdinalIgnoreCase))
        {
            return Unsupported(windowDays, threshold,
                "Anomaly policies compare against a rolling baseline that is itself derived from history, so replaying them would not reflect how they will behave going forward.");
        }

        if (policy.Kind.Equals("activity", StringComparison.OrdinalIgnoreCase))
            return await BacktestActivityAsync(policy, from, to, windowDays, threshold, evalInterval, ct);

        return await BacktestMetricAsync(policy, from, windowDays, threshold, ct);
    }

    private async Task<Result> BacktestActivityAsync(
        AlertPolicy policy, DateTimeOffset from, DateTimeOffset to,
        int windowDays, int threshold, TimeSpan evalInterval, CancellationToken ct)
    {
        var pattern = (policy.ActivityPattern ?? "").Trim();
        if (pattern.Length == 0)
            return Unsupported(windowDays, threshold, "This activity policy has no activity pattern set.");

        var like = pattern.Replace("*", "%");
        var window = TimeSpan.FromMinutes(Math.Max(1, policy.WindowMinutes));

        // Load once and count in memory — the alternative is one query per step.
        // Reach back a full window before the range so the first steps are correct.
        var times = await db.AuditEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= from - window && e.OccurredAt <= to
                        && EF.Functions.Like(e.Activity, like))
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.OccurredAt)
            .ToListAsync(ct);

        var earliest = await db.AuditEvents.AsNoTracking()
            .OrderBy(e => e.OccurredAt).Select(e => (DateTimeOffset?)e.OccurredAt).FirstOrDefaultAsync(ct);
        if (earliest is null)
            return Unsupported(windowDays, threshold, "No tenant audit events have been collected yet, so there is nothing to replay.");

        // Never imply coverage older than the data. Retention trims audit events.
        var effectiveFrom = earliest > from ? earliest.Value : from;
        var effectiveDays = (int)Math.Ceiling((to - effectiveFrom).TotalDays);

        var step = evalInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(15) : evalInterval;
        var outcome = BacktestMath.CountEpisodes(times, effectiveFrom, to, window, step, threshold);
        var steps = (int)Math.Max(1, (to - effectiveFrom).Ticks / step.Ticks);

        return new Result(
            Supported: true,
            UnsupportedReason: null,
            WindowDays: Math.Max(1, effectiveDays),
            Threshold: threshold,
            WouldFireCount: outcome.Episodes,
            MaxObservedValue: outcome.MaxValue,
            SamplesEvaluated: steps,
            Basis: $"Replayed {times.Count} matching audit event(s) over a {policy.WindowMinutes}-minute rolling window.",
            FiredAt: outcome.FiredAt);
    }

    private async Task<Result> BacktestMetricAsync(
        AlertPolicy policy, DateTimeOffset from, int windowDays, int threshold, CancellationToken ct)
    {
        if (!SnapshotMetrics.TryGetValue(policy.Metric ?? "", out var selector))
        {
            return Unsupported(windowDays, threshold,
                $"No historical record exists for '{policy.Metric}'. Only metrics captured in trend snapshots " +
                "(risky users, non-compliant devices, critical and high alert counts) can be replayed.");
        }

        var snapshots = await db.TrendSnapshots.AsNoTracking()
            .Where(s => s.CapturedAt >= from)
            .OrderBy(s => s.CapturedAt)
            .ToListAsync(ct);

        if (snapshots.Count < 2)
            return Unsupported(windowDays, threshold,
                "Not enough trend snapshots in this period to replay the policy. Snapshots accumulate as collections run.");

        var series = snapshots.Select(s => (s.CapturedAt, selector(s))).ToList();
        var outcome = BacktestMath.CountEpisodesFromSeries(series, threshold);
        var covered = (int)Math.Ceiling((DateTimeOffset.UtcNow - snapshots[0].CapturedAt).TotalDays);

        return new Result(
            Supported: true,
            UnsupportedReason: null,
            WindowDays: Math.Max(1, covered),
            Threshold: threshold,
            WouldFireCount: outcome.Episodes,
            MaxObservedValue: outcome.MaxValue,
            SamplesEvaluated: snapshots.Count,
            Basis: $"Replayed {snapshots.Count} trend snapshot(s) of '{policy.Metric}'. Accurate to snapshot granularity, not every evaluation cycle.",
            FiredAt: outcome.FiredAt);
    }

    private static Result Unsupported(int windowDays, int threshold, string reason) =>
        new(false, reason, windowDays, threshold, 0, 0, 0, "", []);
}
