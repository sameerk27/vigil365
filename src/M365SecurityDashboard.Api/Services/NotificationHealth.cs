using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Derives per-channel delivery health from notification log rows. Pure functions
/// so the failure-detection logic is unit-testable without a database.
/// </summary>
public static class NotificationHealth
{
    public sealed record ChannelHealth(
        string Channel,
        int ConsecutiveFailures,
        bool Healthy,
        DateTimeOffset? LastAttemptAt,
        DateTimeOffset? LastSuccessAt,
        string? LastError);

    /// <summary>
    /// Computes health per channel. "Consecutive failures" counts the most-recent
    /// unbroken run of failed attempts (reset by any success). A channel with no
    /// attempts at all is reported healthy.
    /// </summary>
    public static IReadOnlyList<ChannelHealth> Compute(IEnumerable<NotificationLog> logs)
    {
        return logs
            .GroupBy(l => l.Channel)
            .Select(g =>
            {
                var recent = g.OrderByDescending(l => l.SentAt).ToList();
                var consecutive = 0;
                foreach (var row in recent)
                {
                    if (row.Success) break;
                    consecutive++;
                }
                var lastSuccess = recent.FirstOrDefault(l => l.Success);
                var lastFail = recent.FirstOrDefault(l => !l.Success);
                return new ChannelHealth(
                    g.Key,
                    consecutive,
                    Healthy: consecutive == 0,
                    LastAttemptAt: recent.FirstOrDefault()?.SentAt,
                    LastSuccessAt: lastSuccess?.SentAt,
                    LastError: consecutive > 0 ? lastFail?.Error : null);
            })
            .OrderBy(h => h.Channel)
            .ToList();
    }

    /// <summary>Channels whose consecutive-failure count is at or above the threshold.</summary>
    public static IReadOnlyList<ChannelHealth> FailingChannels(IEnumerable<NotificationLog> logs, int threshold) =>
        Compute(logs).Where(h => h.ConsecutiveFailures >= Math.Max(1, threshold)).ToList();
}
