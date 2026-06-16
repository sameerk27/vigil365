using M365SecurityDashboard.Api.Models;
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
            if (options.Value.IsConfigured())
            {
                try
                {
                    using var scope = services.CreateScope();
                    var collector = scope.ServiceProvider.GetRequiredService<GraphCollector>();
                    var run = await collector.CollectAsync(stoppingToken);
                    logger.LogInformation(
                        "Collection run {RunId} completed: {Upserted} alerts, {Failures} source failures",
                        run.Id, run.AlertsUpserted, run.SourceFailures);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error during Graph collection run");
                }
            }
            else
            {
                logger.LogWarning(
                    "Graph credentials not configured — skipping collection. " +
                    "Set Graph:TenantId, Graph:ClientId, and Graph:ClientSecret.");
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
