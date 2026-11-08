using System.Text.Json;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Reads what the Copilot page needs from Graph and says how complete it is. The usage
/// report is paged: a later page that fails leaves a shorter user list, which would
/// undercount active, idle and never-used users if shown as the whole tenant, so it comes
/// back flagged. Licences are a separate read: when they can't be read in full the seat
/// numbers are unknown, never "no Copilot licence".
/// </summary>
public static class CopilotUsageReader
{
    public const string ReportPath =
        $"/beta/reports/getMicrosoft365CopilotUsageUserDetail(period='{CopilotUsageSummary.ReportPeriod}')?$format=application/json";

    public const string SeatsPartMessage =
        "Only some of the tenant's licences could be read, so seat numbers aren't shown. Refresh to try again.";

    public const string SeatsUnreadMessage =
        "The tenant's licences couldn't be read, so seat numbers aren't shown.";

    /// <param name="Partial">The usage report stopped after its first page or later: the users are only those read.</param>
    /// <param name="SeatsError">Why seat numbers are missing, or null when the licences were read in full.</param>
    public sealed record Reading(CopilotUsageSummary.Result Summary, bool Partial, string? SeatsError);

    /// <summary>Throws when the report's first page can't be read: there is nothing to show then.</summary>
    public static async Task<Reading> ReadAsync(GraphApiClient graph, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        var report = await graph.ReadCollectionAsync(ReportPath, ct);

        IReadOnlyList<JsonElement> skus = [];
        string? seatsError = null;
        try
        {
            var read = await graph.ReadCollectionAsync("/v1.0/subscribedSkus", ct);
            if (read.Complete) skus = read.Items;
            else seatsError = SeatsPartMessage;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Copilot page: licences could not be read");
            seatsError = GraphErrorHint.DescribeOrNull(ex.Message, "Directory.Read.All") ?? SeatsUnreadMessage;
        }

        return new Reading(CopilotUsageSummary.Build(report.Items, skus, now), !report.Complete, seatsError);
    }
}
