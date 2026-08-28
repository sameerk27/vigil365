using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>Real system/operational metrics for the Metrics tab.</summary>
public static class MetricsEndpoints
{
    public static void MapMetricsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/metrics", async (MetricsService metrics, CancellationToken ct) =>
            Results.Ok(await metrics.GatherAsync(ct)))
            .RequireAuthorization("RequireAnalyst");
    }
}
