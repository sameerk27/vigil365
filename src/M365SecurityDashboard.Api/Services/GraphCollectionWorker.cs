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
                    var db = sp.GetRequiredService<AppDbContext>();
                    var health = await db.ClientTenants.FirstOrDefaultAsync(t => t.Id == tenant.Id, ct);

                    // A stored secret the key ring can no longer decrypt is an operator fix,
                    // not a transient fault: backing off would only hide it. Say so and stop.
                    if (health is not null && credentials.OwnSecretUnreadable(health))
                    {
                        health.LastCollectionStatus = ClientHealth.Unreadable;
                        health.LastError = "The stored client secret cannot be decrypted (the encryption key ring changed or was lost). Re-enter this client's credentials on Clients.";
                        await db.SaveChangesAsync(ct);
                        logger.LogWarning("Skipping tenant {TenantName}: its stored client secret cannot be decrypted.", tenant.Name);
                        return;
                    }

                    if (!await credentials.IsConfiguredAsync(ct))
                    {
                        logger.LogDebug("Skipping tenant {TenantName}: no Graph credentials apply.", tenant.Name);
                        return;
                    }

                    try
                    {
                        var collector = sp.GetRequiredService<GraphCollector>();
                        var run = await collector.CollectAsync(ct);
                        Interlocked.Increment(ref collected);
                        if (run.Status == CollectionStatus.Failed)
                        {
                            // Right after consent Microsoft needs a few minutes before Graph knows
                            // the app in the client's tenant. That is not a failure: no backoff,
                            // retry on the next cycle, and say why.
                            if (health is not null && ClientHealth.AllFailuresAreConsentPending(run.SourceFailureDetails))
                            {
                                health.LastCollectionAt = DateTimeOffset.UtcNow;
                                health.LastCollectionStatus = ClientHealth.WaitingForConsent;
                                health.LastError = GraphErrorHint.NotYetProvisionedMessage;
                                health.NextCollectionAfter = null;
                                await db.SaveChangesAsync(ct);
                                logger.LogInformation("Tenant {TenantName}: Microsoft has not finished setting up the app yet; retrying next cycle.", tenant.Name);
                                return;
                            }
                            // Every source failed (revoked consent, expired secret): a failed
                            // collection that backs off, not a success that resets the backoff.
                            throw new InvalidOperationException(run.Error ?? "Every Graph source failed.");
                        }
                        logger.LogInformation(
                            "Collection run {RunId} completed: {Upserted} alerts, {Failures} source failures",
                            run.Id, run.AlertsUpserted, run.SourceFailures);
                        if (health is not null)
                        {
                            // Some sources failing is a partial success: the client shows as partly
                            // connected with the failing sources named, not as healthy.
                            var (status, error) = ClientHealth.OutcomeOf(run.SourceFailures, run.SourceFailureDetails);
                            health.LastCollectionAt = DateTimeOffset.UtcNow;
                            health.LastCollectionStatus = status;
                            health.LastError = error;
                            health.ConsecutiveFailures = 0;
                            health.NextCollectionAfter = null;
                            await db.SaveChangesAsync(ct);
                        }
                    }
                    catch (CollectionBusyException)
                    {
                        // A manual run is already collecting this client: not a failure, so no
                        // backoff and no error on the client. The other pass will report it.
                        logger.LogInformation("Tenant {TenantName}: a collection is already running; skipped this cycle.", tenant.Name);
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (health is not null)
                        {
                            var now = DateTimeOffset.UtcNow;
                            health.LastCollectionAt = now;
                            health.LastCollectionStatus = nameof(CollectionStatus.Failed);
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
