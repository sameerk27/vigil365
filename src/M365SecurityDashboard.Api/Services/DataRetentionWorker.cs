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
                await Data.Tenancy.TenantIterator.ForEachActiveTenantAsync(services, logger, "Retention prune", async (sp, tenant, ct) =>
                {
                    var db = sp.GetRequiredService<AppDbContext>();
                    var summary = await PruneAsync(db, options.Value, ct);
                    if (summary.TotalDeleted > 0)
                    {
                        logger.LogInformation("Retention prune removed {Total} rows: {Summary}",
                            summary.TotalDeleted, summary.Describe());
                        var audit = sp.GetRequiredService<AuditLogger>();
                        await audit.WriteAsync("retention.prune", "database", null, summary.Describe(), ct);
                    }
                    else
                    {
                        logger.LogDebug("Retention prune: nothing to remove.");
                    }
                }, stoppingToken);

                // The audit chain is one sequence across every client, so it is
                // pruned once, with no tenant, rather than in each client's pass.
                using var scope = services.CreateScope();
                var auditEntries = await PruneAuditEntriesAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), options.Value, stoppingToken);
                if (auditEntries > 0)
                {
                    logger.LogInformation("Retention prune removed {Count} audit entries", auditEntries);
                    await scope.ServiceProvider.GetRequiredService<AuditLogger>()
                        .WriteMspAsync("retention.prune", "audit_log", null, $"audit entries {auditEntries}", stoppingToken);
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
    /// One prune pass over the current tenant's data (the audit chain is pruned
    /// separately, see <see cref="PruneAuditEntriesAsync"/>). Batched RemoveRange
    /// (not ExecuteDelete) so it works on every EF provider, including the
    /// in-memory one used by tests; volumes stay small because the job runs daily.
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

        if (o.TenantAuditEventsDays > 0)
        {
            var cutoff = now.AddDays(-o.TenantAuditEventsDays);
            summary.TenantAuditEvents = await DeleteBatchedAsync(db,
                db.AuditEvents.Where(e => e.OccurredAt < cutoff), ct);
        }

        return summary;
    }

    /// <summary>
    /// Prunes the audit chain across every client (active, deactivated or
    /// purged) by deleting a strict prefix in Id order: every entry before the
    /// oldest one still inside the window. Verification starts at the first
    /// surviving entry, so the chain stays valid. Pruning per client instead
    /// would leave an inactive client's old entries between deleted
    /// neighbours, and the next kept entry's PrevHash would point at nothing.
    /// ExecuteDelete because the rows belong to many clients and the write
    /// guard admits a client's row only in that client's context.
    /// </summary>
    public static async Task<int> PruneAuditEntriesAsync(AppDbContext db, RetentionOptions o, CancellationToken ct)
    {
        if (o.AuditEntriesDays <= 0) return 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-o.AuditEntriesDays);
        var chain = db.CrossTenant<AuditEntry>(); /* the whole chain, every tenant */
        var keepFrom = await chain.Where(a => a.Timestamp >= cutoff).MinAsync(a => (long?)a.Id, ct);
        var prefix = keepFrom is long id ? chain.Where(a => a.Id < id) : chain.Where(a => a.Timestamp < cutoff);
        return await prefix.ExecuteDeleteAsync(ct);
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
        public int TenantAuditEvents { get; set; }

        public int TotalDeleted =>
            ResolvedAlerts + TriggeredAlerts + NotificationLogs + CollectionRuns + TrendSnapshots + TenantAuditEvents;

        public string Describe() =>
            $"resolved alerts {ResolvedAlerts}, triggered alerts {TriggeredAlerts}, " +
            $"notification logs {NotificationLogs}, collection runs {CollectionRuns}, " +
            $"trend snapshots {TrendSnapshots}, tenant audit events {TenantAuditEvents}";
    }
}
