using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Nightly data-retention job. Deletes rows older than the configured retention
/// windows (see <see cref="RetentionOptions"/>) so the database stays bounded on
/// long-running installs. Only terminal data is pruned — open alerts and
/// unresolved triggered alerts are always kept regardless of age.
/// </summary>
public sealed class DataRetentionWorker(
    IServiceProvider services,
    IOptions<RetentionOptions> options,
    ILogger<DataRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

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
                var summary = await PruneAsync(db, options.Value, stoppingToken);
                if (summary.TotalDeleted > 0)
                {
                    logger.LogInformation("Retention prune removed {Total} rows: {Summary}",
                        summary.TotalDeleted, summary.Describe());
                    var audit = scope.ServiceProvider.GetRequiredService<AuditLogger>();
                    await audit.WriteAsync("retention.prune", "database", null, summary.Describe(), stoppingToken);
                }
                else
                {
                    logger.LogDebug("Retention prune: nothing to remove.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Data retention prune failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One prune pass. Batched RemoveRange (not ExecuteDelete) so it works on every
    /// EF provider, including the in-memory one used by tests; volumes stay small
    /// because the job runs daily.
    /// </summary>
    public static async Task<PruneSummary> PruneAsync(AppDbContext db, RetentionOptions o, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new PruneSummary();

        if (o.ResolvedAlertsDays > 0)
        {
            var cutoff = now.AddDays(-o.ResolvedAlertsDays);
            summary.ResolvedAlerts = await DeleteBatchedAsync(db,
                db.SecurityAlerts.Where(a => a.IsResolved && a.LastUpdatedAt < cutoff), ct);
        }

        if (o.TriggeredAlertsDays > 0)
        {
            var cutoff = now.AddDays(-o.TriggeredAlertsDays);
            summary.TriggeredAlerts = await DeleteBatchedAsync(db,
                db.TriggeredAlerts.Where(t =>
                    (t.Status == "resolved" || t.Status == "auto_resolved") && t.TriggeredAt < cutoff), ct);
        }

        if (o.NotificationLogsDays > 0)
        {
            var cutoff = now.AddDays(-o.NotificationLogsDays);
            summary.NotificationLogs = await DeleteBatchedAsync(db,
                db.NotificationLogs.Where(l => l.SentAt < cutoff), ct);
        }

        if (o.CollectionRunsDays > 0)
        {
            var cutoff = now.AddDays(-o.CollectionRunsDays);
            summary.CollectionRuns = await DeleteBatchedAsync(db,
                db.CollectionRuns.Where(r => r.StartedAt < cutoff), ct);
        }

        if (o.TrendSnapshotsDays > 0)
        {
            var cutoff = now.AddDays(-o.TrendSnapshotsDays);
            summary.TrendSnapshots = await DeleteBatchedAsync(db,
                db.TrendSnapshots.Where(t => t.CapturedAt < cutoff), ct);
        }

        if (o.AuditEntriesDays > 0)
        {
            var cutoff = now.AddDays(-o.AuditEntriesDays);
            summary.AuditEntries = await DeleteBatchedAsync(db,
                db.AuditEntries.Where(a => a.Timestamp < cutoff), ct);
        }

        if (o.TenantAuditEventsDays > 0)
        {
            var cutoff = now.AddDays(-o.TenantAuditEventsDays);
            summary.TenantAuditEvents = await DeleteBatchedAsync(db,
                db.AuditEvents.Where(e => e.OccurredAt < cutoff), ct);
        }

        return summary;
    }

    private static async Task<int> DeleteBatchedAsync<T>(AppDbContext db, IQueryable<T> query, CancellationToken ct)
        where T : class
    {
        const int batchSize = 5000;
        var deleted = 0;
        while (true)
        {
            var batch = await query.Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            db.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            deleted += batch.Count;
            if (batch.Count < batchSize) break;
        }
        return deleted;
    }

    public sealed class PruneSummary
    {
        public int ResolvedAlerts { get; set; }
        public int TriggeredAlerts { get; set; }
        public int NotificationLogs { get; set; }
        public int CollectionRuns { get; set; }
        public int TrendSnapshots { get; set; }
        public int AuditEntries { get; set; }
        public int TenantAuditEvents { get; set; }

        public int TotalDeleted =>
            ResolvedAlerts + TriggeredAlerts + NotificationLogs + CollectionRuns + TrendSnapshots + AuditEntries + TenantAuditEvents;

        public string Describe() =>
            $"resolved alerts {ResolvedAlerts}, triggered alerts {TriggeredAlerts}, " +
            $"notification logs {NotificationLogs}, collection runs {CollectionRuns}, " +
            $"trend snapshots {TrendSnapshots}, audit entries {AuditEntries}, " +
            $"tenant audit events {TenantAuditEvents}";
    }
}
