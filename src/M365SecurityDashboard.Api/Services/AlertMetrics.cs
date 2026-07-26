using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Operational metrics over the triggered-alert workflow — MTTA, MTTR,
/// resolution rate, and per-assignee workload. Pure and static so the maths is
/// unit-testable without a database; the endpoint just supplies the rows.
///
/// The data already existed (TriggeredAt / AcknowledgedAt / ResolvedAt); this
/// only reads it. Auto-resolved alerts count toward resolution but not toward
/// analyst workload, since no person acted on them.
/// </summary>
public static class AlertMetrics
{
    public sealed record AssigneeLoad(string Assignee, int Open, int Acknowledged, int Resolved);

    public sealed record Result(
        int Total,
        int Open,
        int Resolved,
        int AutoResolved,
        double ResolutionRatePct,
        double? MttaMinutes,
        double? MttrMinutes,
        int Acknowledged,
        IReadOnlyList<AssigneeLoad> ByAssignee);

    /// <summary>Statuses that mean the alert is no longer demanding attention.</summary>
    private static bool IsResolved(string status) =>
        status is "resolved" or "auto_resolved";

    public static Result Compute(IReadOnlyList<TriggeredAlert> alerts)
    {
        var total = alerts.Count;
        var resolved = alerts.Count(a => a.Status == "resolved");
        var autoResolved = alerts.Count(a => a.Status == "auto_resolved");
        var open = alerts.Count(a => !IsResolved(a.Status));
        var acknowledged = alerts.Count(a => a.AcknowledgedAt is not null);

        // Mean time to acknowledge: only alerts a person actually acknowledged.
        var ttaSamples = alerts
            .Where(a => a.AcknowledgedAt is not null && a.AcknowledgedAt >= a.TriggeredAt)
            .Select(a => (a.AcknowledgedAt!.Value - a.TriggeredAt).TotalMinutes)
            .ToList();

        // Mean time to resolve: alerts with a resolve timestamp (manual or auto).
        var ttrSamples = alerts
            .Where(a => a.ResolvedAt is not null && a.ResolvedAt >= a.TriggeredAt)
            .Select(a => (a.ResolvedAt!.Value - a.TriggeredAt).TotalMinutes)
            .ToList();

        var byAssignee = alerts
            .Where(a => !string.IsNullOrWhiteSpace(a.AssignedTo))
            .GroupBy(a => a.AssignedTo!)
            .Select(g => new AssigneeLoad(
                g.Key,
                Open: g.Count(a => !IsResolved(a.Status)),
                Acknowledged: g.Count(a => a.AcknowledgedAt is not null && !IsResolved(a.Status)),
                Resolved: g.Count(a => IsResolved(a.Status))))
            .OrderByDescending(x => x.Open)
            .ThenByDescending(x => x.Resolved)
            .ToList();

        return new Result(
            Total: total,
            Open: open,
            Resolved: resolved,
            AutoResolved: autoResolved,
            ResolutionRatePct: total == 0 ? 0 : Math.Round((resolved + autoResolved) * 100.0 / total, 1),
            MttaMinutes: ttaSamples.Count == 0 ? null : Math.Round(ttaSamples.Average(), 1),
            MttrMinutes: ttrSamples.Count == 0 ? null : Math.Round(ttrSamples.Average(), 1),
            Acknowledged: acknowledged,
            ByAssignee: byAssignee);
    }
}
