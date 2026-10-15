using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

public sealed class GraphCollectionWorker(
    IServiceProvider services,
    IOptions<GraphOptions> options,
    ILogger<GraphCollectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Value.CollectionIntervalMinutes);
        logger.LogInformation("Graph collection worker started. Interval: {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // One scope per active tenant; each resolves its own credentials.
                // A tenant with none is skipped (and says so once per cycle), so an
                // MSP install collects whichever clients are connected and a
                // single-tenant install behaves exactly as before.
                var collected = 0;
                var startedAt = DateTimeOffset.UtcNow;
                var maxBackoff = TimeSpan.FromMinutes(Math.Max(1, options.Value.MaxBackoffMinutes));
                await TenantIterator.ForEachActiveTenantAsync(services, logger, "Graph collection", async (sp, tenant, ct) =>
                {
                    if (!CollectionBackoff.IsDue(startedAt, tenant.NextCollectionAfter))
                    {
                        logger.LogDebug("Skipping tenant {TenantName}: backing off until {Next} after {Failures} failure(s).",
                            tenant.Name, tenant.NextCollectionAfter, tenant.ConsecutiveFailures);
                        return;
                    }
                    var credentials = sp.GetRequiredService<TenantGraphCredentials>();
                    if (!await credentials.IsConfiguredAsync(ct))
                    {
                        logger.LogDebug("Skipping tenant {TenantName}: no Graph credentials apply.", tenant.Name);
                        return;
                    }

                    var db = sp.GetRequiredService<AppDbContext>();
                    var health = await db.ClientTenants.FirstOrDefaultAsync(t => t.Id == tenant.Id, ct);
                    try
                    {
                        var collector = sp.GetRequiredService<GraphCollector>();
                        var run = await collector.CollectAsync(ct);
                        Interlocked.Increment(ref collected);
                        // Every source failed (revoked consent, expired secret): a failed
                        // collection that backs off, not a success that resets the backoff.
                        if (run.Status == CollectionStatus.Failed)
                            throw new InvalidOperationException(run.Error ?? "Every Graph source failed.");
                        logger.LogInformation(
                            "Collection run {RunId} completed: {Upserted} alerts, {Failures} source failures",
                            run.Id, run.AlertsUpserted, run.SourceFailures);
                        if (health is not null)
                        {
                            health.LastCollectionAt = DateTimeOffset.UtcNow;
                            health.LastCollectionStatus = run.Status.ToString();
                            health.LastError = run.SourceFailures > 0 ? run.Error : null;
                            health.ConsecutiveFailures = 0;
                            health.NextCollectionAfter = null;
                            await db.SaveChangesAsync(ct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (health is not null)
                        {
                            var now = DateTimeOffset.UtcNow;
                            health.LastCollectionAt = now;
                            health.LastCollectionStatus = "Failed";
                            health.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                            health.ConsecutiveFailures++;
                            health.NextCollectionAfter = CollectionBackoff.NextAttempt(now, health.ConsecutiveFailures, interval, maxBackoff);
                            await db.SaveChangesAsync(ct);
                            logger.LogWarning("Tenant {TenantName} failed {Failures} time(s) in a row; next attempt after {Next}.",
                                tenant.Name, health.ConsecutiveFailures, health.NextCollectionAfter);
                        }
                        throw; // TenantIterator logs it and moves to the next tenant
                    }

                    // Evaluate alert policies against the freshly collected data and
                    // dispatch notifications — runs even when no browser is open.
                    try
                    {
                        var evaluator = sp.GetRequiredService<AlertEvaluator>();
                        await evaluator.EvaluateAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Alert policy evaluation failed");
                    }
                }, stoppingToken,
                    maxParallel: Math.Max(1, options.Value.TenantParallelism),
                    stagger: TimeSpan.FromSeconds(Math.Max(0, options.Value.TenantStaggerSeconds)));

                if (collected == 0)
                    logger.LogWarning(
                        "No tenant has Graph credentials — nothing collected. Configure Graph in the setup " +
                        "wizard (single tenant) or set credentials per tenant via /api/tenants.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error during Graph collection run");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Graph collection worker stopped.");
    }
}
