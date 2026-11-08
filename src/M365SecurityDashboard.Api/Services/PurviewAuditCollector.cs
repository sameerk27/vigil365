using System.Net;
using System.Text.Json;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static M365SecurityDashboard.Api.Services.Common.JsonRead;
using M365SecurityDashboard.Api.Services.Common;

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
    /// <summary>Power Platform admin operations whose record type Graph can't filter on (see PurviewAuditNormalizer.PowerPlatformAdminOperations).</summary>
    public const string FeedPowerPlatformAdmin = "powerPlatformAdmin";
    public static readonly IReadOnlyList<string> Feeds = [FeedPowerPlatform, FeedCopilotStudio, FeedPowerPlatformAdmin];

    public const string StatusWaiting = "Waiting";
    public const string StatusOk = "Ok";
    public const string StatusNeedsPermission = "NeedsPermission";
    public const string StatusFailed = "Failed";

    // Microsoft may also refuse with 403 when the permission is granted but audit search isn't
    // available to the tenant (assumed from the docs, not seen on a live tenant), so the
    // message covers both and carries Microsoft's own sentence when there is one.
    public const string NeedsPermissionMessage =
        "Copilot agent and Power Platform alerts need AuditLogsQuery.Read.All (read-only). Grant admin consent, then wait for the next run. " +
        "If it is already granted, check that auditing is turned on in Microsoft Purview.";

    public const string DomainsUnreadableMessage =
        "Couldn't read the organisation's verified domains, so a flow shared with someone outside it can't be recognised. " +
        "These audit records will be read again on the next run.";

    private readonly GraphOptions _options = options.Value;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private VerifiedDomains? _verifiedDomains;

    /// <summary>The tenant's verified domains, or why they couldn't be read in full.</summary>
    private sealed record VerifiedDomains(IReadOnlySet<string> Names, string? Problem);

    /// <summary>A step that can't finish this run without being wrong. The search stays pending and the reason is shown as is.</summary>
    private sealed class RetryNextRunException(string message) : Exception(message);

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
            state.LastError = GraphErrorHint.GraphMessageOrNull(ex.Message) is { } said
                ? TextLimits.Truncate($"{NeedsPermissionMessage} Microsoft said: {said}", 1000)
                : NeedsPermissionMessage;
            state.PendingQueryId = null;
            state.LastSubmittedAt = now; // try again on the normal cadence, not every run
            logger.LogInformation("Purview audit feed {Feed}: permission not granted", feed);
        }
        catch (RetryNextRunException ex)
        {
            state.Status = StatusFailed;
            state.LastError = TextLimits.Truncate(ex.Message, 1000);
            logger.LogWarning("Purview audit feed {Feed}: {Reason}", feed, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Status = StatusFailed;
            state.LastError = TextLimits.Truncate(GraphErrorHint.Describe(ex.Message, "Purview audit search"), 1000);
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

    /// <summary>
    /// The search for one feed: Power Platform by record type; Copilot Studio and the Power
    /// Platform admin operations by their documented operation names.
    /// </summary>
    internal static object QueryBody(string feed, DateTimeOffset from, DateTimeOffset to)
    {
        var start = from.UtcDateTime.ToString("O");
        var end = to.UtcDateTime.ToString("O");
        var name = $"Vigil365 {feed} {start}";
        return feed switch
        {
            FeedCopilotStudio => new { displayName = name, filterStartDateTime = start, filterEndDateTime = end, operationFilters = PurviewAuditNormalizer.CopilotStudioOperations },
            FeedPowerPlatformAdmin => new { displayName = name, filterStartDateTime = start, filterEndDateTime = end, operationFilters = PurviewAuditNormalizer.PowerPlatformAdminOperations },
            _ => (object)new { displayName = name, filterStartDateTime = start, filterEndDateTime = end, recordTypeFilters = PurviewAuditNormalizer.PowerPlatformRecordTypes },
        };
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
            if (!read.Complete) throw new RetryNextRunException("The audit search results could not be read in full. Trying again next run.");

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
        // The domains decide one thing: whether a flow shared with someone who isn't a guest
        // went outside the organisation. Without them that share would be stored as internal
        // and its high-severity rule would never fire, so such a batch waits for the next run.
        // Guests (#EXT#) are external either way, and every other record doesn't need them.
        bool IsExternal(string upn) =>
            domains.Problem is null ? PurviewAuditNormalizer.IsExternal(upn, domains.Names)
            : PurviewAuditNormalizer.IsExternal(upn, new HashSet<string>()) ? true
            : throw new RetryNextRunException($"{DomainsUnreadableMessage} {domains.Problem}".TrimEnd());

        var connectors = _options.PowerPlatformHighRiskConnectors ?? [];
        var skipped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var collectedAt = _time.GetUtcNow();
        // Normalise the whole batch before storing any of it, so a batch that has to wait leaves nothing half-stored.
        var normalized = new List<(string Id, JsonElement Record, NormalizedAuditEvent Event)>();
        foreach (var r in records)
        {
            var id = Str(r, "id");
            if (id is null || !known.Add(id)) continue; // overlap with the previous window, or a duplicate in this one

            var e = PurviewAuditNormalizer.Normalize(r, connectors, IsExternal);
            if (e is null)
            {
                skipped.Add(Str(r, "operation") ?? "(no operation)");
                continue;
            }
            normalized.Add((id, r, e));
        }

        foreach (var (id, r, e) in normalized)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                ExternalId = TextLimits.Truncate(id, 256),
                Source = PurviewAuditNormalizer.Source,
                Activity = TextLimits.Truncate(e.Activity, 200),
                Category = TextLimits.Truncate(e.Product, 80),
                ActorUpn = TextLimits.TruncateOrNull(e.ActorUpn, 320),
                TargetName = TextLimits.TruncateOrNull(e.TargetName, 320),
                Result = TextLimits.TruncateOrNull(e.Result, 20),
                OccurredAt = Date(r, "createdDateTime") ?? collectedAt,
                CollectedAt = collectedAt,
                RawJson = r.GetRawText(),
            });
        }

        // Operation names nothing matched: what to look at when tuning against a live tenant.
        if (skipped.Count > 0)
            logger.LogInformation("Purview audit: skipped operations {Operations}", string.Join(", ", skipped.Take(40)));
        if (normalized.Count > 0)
            logger.LogInformation("Purview audit: stored {Count} new events", normalized.Count);
        return normalized.Count;
    }

    /// <summary>
    /// The tenant's verified domains, read once per run when a batch needs them. A list that
    /// failed or came back short is kept with the reason, never treated as the whole list: a
    /// missing domain would make colleagues look external, and no list would make everyone
    /// but guests look internal.
    /// </summary>
    private async Task<VerifiedDomains> VerifiedDomainsAsync(CancellationToken ct)
    {
        if (_verifiedDomains is { Problem: null }) return _verifiedDomains;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var read = await graph.ReadCollectionAsync("/v1.0/domains?$select=id,isVerified", ct);
            foreach (var d in read.Items)
                if (d.TryGetProperty("isVerified", out var v) && v.ValueKind == JsonValueKind.True && Str(d, "id") is { } name)
                    set.Add(name);
            return _verifiedDomains = new VerifiedDomains(set, read.Complete ? null : "Only part of the list came back.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read verified domains");
            return _verifiedDomains = new VerifiedDomains(set, GraphErrorHint.DescribeOrNull(ex.Message, "Directory.Read.All") ?? "");
        }
    }

}
