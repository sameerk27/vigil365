using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Hourly maintenance for outbound notifications:
///  1. Digest mode — once per day at DigestHourUtc, batches the alerts triggered
///     since the last digest into a single rollup per digest-enabled channel.
///  2. Delivery-failure alerting — when a channel accumulates enough consecutive
///     failed attempts, logs a warning and (debounced) notifies via any healthy
///     channel so a silently-broken webhook doesn't go unnoticed.
/// </summary>
public sealed class NotificationDigestWorker(
    IServiceProvider services,
    ILogger<NotificationDigestWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureAlertDebounce = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var sender = scope.ServiceProvider.GetRequiredService<NotificationSender>();
                await TickAsync(db, sender, DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification digest tick failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One maintenance pass. Public + parameterized on `now` for testability.</summary>
    public async Task TickAsync(AppDbContext db, NotificationSender sender, DateTimeOffset now, CancellationToken ct)
    {
        var cfg = await db.NotificationSettings.FirstOrDefaultAsync(ct);
        if (cfg is null) return;

        await MaybeSendDigestAsync(db, sender, cfg, now, ct);
        await MaybeAlertOnFailuresAsync(db, sender, cfg, now, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task MaybeSendDigestAsync(AppDbContext db, NotificationSender sender, NotificationSettings cfg, DateTimeOffset now, CancellationToken ct)
    {
        if (!ShouldSendDigest(cfg, now)) return;

        var since = cfg.LastDigestAt ?? now.AddDays(-1);
        var candidates = await db.TriggeredAlerts.AsNoTracking()
            .Where(a => a.TriggeredAt > since)
            .ToListAsync(ct);
        var pending = PendingForDigest(candidates, cfg.MinSeverity);

        cfg.LastDigestAt = now;
        if (pending.Count == 0)
        {
            logger.LogDebug("Digest hour reached but no pending alerts to roll up.");
            return;
        }
        var sent = await sender.SendDigestRollupAsync(db, cfg, pending, ct);
        logger.LogInformation("Sent daily digest of {Count} alerts to {Channels} channel(s).", pending.Count, sent);
    }

    private async Task MaybeAlertOnFailuresAsync(AppDbContext db, NotificationSender sender, NotificationSettings cfg, DateTimeOffset now, CancellationToken ct)
    {
        // Inspect a bounded recent window so a long-ago failure streak can't linger.
        var recent = await db.NotificationLogs.AsNoTracking()
            .OrderByDescending(l => l.SentAt).Take(200).ToListAsync(ct);
        var failing = NotificationHealth.FailingChannels(recent, cfg.FailureAlertThreshold);
        if (failing.Count == 0) return;

        if (cfg.LastFailureAlertAt is { } lastAlert && now - lastAlert < FailureAlertDebounce) return;

        var summary = string.Join("; ", failing.Select(f => $"{f.Channel} ({f.ConsecutiveFailures} consecutive failures)"));
        logger.LogWarning("Notification delivery failing: {Summary}", summary);
        cfg.LastFailureAlertAt = now;

        // Best-effort heads-up through a channel that is still healthy. If email is the
        // broken one, this simply no-ops on that channel.
        var notice = new TriggeredAlert
        {
            Id = Guid.NewGuid(),
            PolicyName = "Notification delivery failure",
            Severity = "high",
            Category = "system",
            Condition = $"Delivery failing on: {summary}",
            MetricValue = failing.Sum(f => f.ConsecutiveFailures),
            Threshold = Math.Max(1, cfg.FailureAlertThreshold),
            TriggeredAt = now,
            Status = "new",
        };
        var failingChannels = failing.Select(f => f.Channel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await sender.DispatchDeliveryFailureAsync(db, cfg, notice, failingChannels, ct);
    }

    /// <summary>
    /// Whether a digest is due at <paramref name="now"/>: at least one channel is in
    /// digest mode, the hour matches, and no digest has yet been sent today.
    /// </summary>
    public static bool ShouldSendDigest(NotificationSettings cfg, DateTimeOffset now)
    {
        if (!(cfg.TeamsDigest || cfg.EmailDigest || cfg.WebhookDigest)) return false;
        if (now.Hour != Math.Clamp(cfg.DigestHourUtc, 0, 23)) return false;
        // Only one digest per calendar day, even though the worker ticks hourly.
        if (cfg.LastDigestAt is { } last && last.UtcDateTime.Date == now.UtcDateTime.Date) return false;
        return true;
    }

    /// <summary>Filters candidate alerts to those at or above the configured minimum severity, most-severe first.</summary>
    public static List<TriggeredAlert> PendingForDigest(IEnumerable<TriggeredAlert> candidates, string minSeverity)
    {
        var minRank = Rank(minSeverity);
        return candidates
            .Where(a => Rank(a.Severity) >= minRank)
            .OrderByDescending(a => Rank(a.Severity)).ThenByDescending(a => a.TriggeredAt)
            .ToList();
    }

    private static readonly Dictionary<string, int> SeverityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["informational"] = 0, ["low"] = 1, ["medium"] = 2, ["high"] = 3, ["critical"] = 4,
    };
    private static int Rank(string? sev) => SeverityRank.TryGetValue(sev ?? "low", out var r) ? r : 1;
}
