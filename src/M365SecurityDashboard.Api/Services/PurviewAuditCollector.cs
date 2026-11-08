using System.Net;
using System.Text.Json;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Reads Copilot Studio agent and Power Platform activity from the Microsoft Purview
/// audit log into <see cref="AuditEvent"/>, where activity policies alert on it.
///
/// The audit search is asynchronous and rate-limited, so each feed is a small state
/// machine on <see cref="PurviewAuditState"/>, advanced once per collection run:
/// submit a search for [watermark - overlap, now]; on a later run, if Microsoft has
/// finished it, store the records and move the watermark. A run never waits for a
/// search. The permission (AuditLogsQuery.Read.All) is optional: when it is missing
/// the feed says "NeedsPermission" and the collection run is not marked as failing.
/// </summary>
public sealed class PurviewAuditCollector(
    AppDbContext db,
    GraphApiClient graph,
    IOptions<GraphOptions> options,
    ILogger<PurviewAuditCollector> logger,
    TimeProvider? time = null)
{
    public const string FeedPowerPlatform = "powerPlatform";
    public const string FeedCopilotStudio = "copilotStudio";
    public static readonly IReadOnlyList<string> Feeds = [FeedPowerPlatform, FeedCopilotStudio];

    public const string StatusWaiting = "Waiting";
    public const string StatusOk = "Ok";
    public const string StatusNeedsPermission = "NeedsPermission";
    public const string StatusFailed = "Failed";

    public const string NeedsPermissionMessage =
        "Copilot agent and Power Platform alerts need AuditLogsQuery.Read.All (read-only). Grant admin consent, then wait for the next run.";

    private readonly GraphOptions _options = options.Value;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlySet<string>? _verifiedDomains;

    /// <summary>Advance every feed one step. Never throws for a Graph failure: the feed's state records it.</summary>
    public async Task CollectAsync(CancellationToken ct)
    {
        if (_options.PurviewAuditIntervalMinutes <= 0) return;
        foreach (var feed in Feeds)
            await StepAsync(feed, ct);
    }

    private async Task StepAsync(string feed, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var state = await db.PurviewAuditStates.FirstOrDefaultAsync(s => s.Feed == feed, ct);
        if (state is null)
        {
            state = new PurviewAuditState { Feed = feed };
            db.PurviewAuditStates.Add(state);
        }

        try
        {
            if (state.PendingQueryId is not null)
            {
                await PollAsync(state, now, ct);
            }
            else if (state.LastSubmittedAt is null || now - state.LastSubmittedAt >= TimeSpan.FromMinutes(_options.PurviewAuditIntervalMinutes))
            {
                await SubmitAsync(state, feed, now, ct);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            state.Status = StatusNeedsPermission;
            state.LastError = NeedsPermissionMessage;
            state.PendingQueryId = null;
            state.LastSubmittedAt = now; // try again on the normal cadence, not every run
            logger.LogInformation("Purview audit feed {Feed}: permission not granted", feed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Status = StatusFailed;
            state.LastError = Trim(GraphErrorHint.Describe(ex.Message, "Purview audit search"), 1000);
            logger.LogWarning(ex, "Purview audit feed {Feed} failed", feed);
        }

        state.LastRunAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task SubmitAsync(PurviewAuditState state, string feed, DateTimeOffset now, CancellationToken ct)
    {
        var from = state.Watermark is { } w
            ? w.AddMinutes(-Math.Max(0, _options.PurviewAuditOverlapMinutes))
            : now.AddHours(-Math.Max(1, _options.PurviewAuditLookbackHours));
        var body = QueryBody(feed, from, now);

        var created = await graph.PostAsync(GraphApiClient.AuditLogQueriesPath, body, ct);
        var id = Str(created, "id") ?? throw new InvalidOperationException("Microsoft did not return an id for the audit search.");

        state.PendingQueryId = id;
        state.PendingFrom = from;
        state.PendingTo = now;
        state.PendingSubmittedAt = now;
        state.LastSubmittedAt = now;
        state.Status = StatusWaiting;
        state.LastError = null;
    }

    /// <summary>The search for one feed: Power Platform by record type, Copilot Studio by its documented operation names.</summary>
    internal static object QueryBody(string feed, DateTimeOffset from, DateTimeOffset to)
    {
        var start = from.UtcDateTime.ToString("O");
        var end = to.UtcDateTime.ToString("O");
        var name = $"Vigil365 {feed} {start}";
        return feed == FeedCopilotStudio
            ? new { displayName = name, filterStartDateTime = start, filterEndDateTime = end, operationFilters = PurviewAuditNormalizer.CopilotStudioOperations }
            : new { displayName = name, filterStartDateTime = start, filterEndDateTime = end, recordTypeFilters = PurviewAuditNormalizer.PowerPlatformRecordTypes };
    }

    private async Task PollAsync(PurviewAuditState state, DateTimeOffset now, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(state.PendingQueryId!);
        var query = (await graph.GetSinglePageAsync($"/v1.0/security/auditLog/queries/{id}", ct)).FirstOrDefault();
        var status = Str(query, "status") ?? "";

        if (status.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
        {
            var read = await graph.ReadCollectionAsync($"/v1.0/security/auditLog/queries/{id}/records", ct);
            // A truncated read proves nothing about what's missing: keep the search and read it again next run.
            if (!read.Complete) throw new InvalidOperationException("The audit search results could not be read in full. Trying again next run.");

            state.LastEventCount = await StoreAsync(read.Items, ct);
            state.Watermark = state.PendingTo;
            state.LastSuccessAt = now;
            state.Status = StatusOk;
            state.LastError = null;
            ClearPending(state);
            return;
        }

        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase) || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
        {
            state.Status = StatusFailed;
            state.LastError = $"Microsoft reports the audit search as {status}. It will be submitted again.";
            ClearPending(state);
            return;
        }

        // notStarted / running: wait, unless Microsoft has sat on it too long.
        if (state.PendingSubmittedAt is { } submitted && now - submitted > TimeSpan.FromMinutes(_options.PurviewAuditQueryTimeoutMinutes))
        {
            state.Status = StatusFailed;
            state.LastError = "The audit search did not finish in time. It will be submitted again.";
            ClearPending(state);
            state.LastSubmittedAt = null; // resubmit on the next run
            return;
        }
        state.Status = StatusWaiting;
    }

    private static void ClearPending(PurviewAuditState state)
    {
        state.PendingQueryId = null;
        state.PendingFrom = null;
        state.PendingTo = null;
        state.PendingSubmittedAt = null;
    }

    private async Task<int> StoreAsync(IReadOnlyList<JsonElement> records, CancellationToken ct)
    {
        if (records.Count == 0) return 0;
        var incomingIds = records.Select(r => Str(r, "id")).OfType<string>().Distinct().ToList();
        var known = (await db.AuditEvents
            .Where(e => e.Source == PurviewAuditNormalizer.Source && incomingIds.Contains(e.ExternalId))
            .Select(e => e.ExternalId)
            .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var domains = await VerifiedDomainsAsync(ct);
        var connectors = _options.PowerPlatformHighRiskConnectors ?? [];
        var skipped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var collectedAt = _time.GetUtcNow();
        foreach (var r in records)
        {
            var id = Str(r, "id");
            if (id is null || !known.Add(id)) continue; // overlap with the previous window, or a duplicate in this one

            var e = PurviewAuditNormalizer.Normalize(r, connectors, upn => PurviewAuditNormalizer.IsExternal(upn, domains));
            if (e is null)
            {
                skipped.Add(Str(r, "operation") ?? "(no operation)");
                continue;
            }

            db.AuditEvents.Add(new AuditEvent
            {
                ExternalId = Trim(id, 256),
                Source = PurviewAuditNormalizer.Source,
                Activity = Trim(e.Activity, 200),
                Category = Trim(e.Product, 80),
                ActorUpn = TrimOrNull(e.ActorUpn, 320),
                TargetName = TrimOrNull(e.TargetName, 320),
                Result = TrimOrNull(e.Result, 20),
                OccurredAt = Date(r, "createdDateTime") ?? collectedAt,
                CollectedAt = collectedAt,
                RawJson = r.GetRawText(),
            });
            added++;
        }

        // Operation names nothing matched: what to look at when tuning against a live tenant.
        if (skipped.Count > 0)
            logger.LogInformation("Purview audit: skipped operations {Operations}", string.Join(", ", skipped.Take(40)));
        if (added > 0)
            logger.LogInformation("Purview audit: stored {Count} new events", added);
        return added;
    }

    /// <summary>The tenant's verified domains, once per run. Unreadable: empty, so only guest (#EXT#) accounts count as external.</summary>
    private async Task<IReadOnlySet<string>> VerifiedDomainsAsync(CancellationToken ct)
    {
        if (_verifiedDomains is not null) return _verifiedDomains;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in await graph.GetCollectionAsync("/v1.0/domains?$select=id,isVerified", ct))
                if (d.TryGetProperty("isVerified", out var v) && v.ValueKind == JsonValueKind.True && Str(d, "id") is { } name)
                    set.Add(name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read verified domains; only guest accounts will count as external");
        }
        return _verifiedDomains = set;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
    private static string? TrimOrNull(string? s, int max) => s is null ? null : Trim(s, max);
}
