using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Assembles an investigation profile for a single entity (a user UPN or a device
/// name) from already-collected data: its security alerts and the tenant audit
/// activity it took part in, merged into one reverse-chronological timeline. This
/// is the dashboard → investigation-tool drill-down. Read-only; local data only.
/// </summary>
public sealed class EntityProfileBuilder(AppDbContext db)
{
    public sealed record TimelineItem(
        DateTimeOffset At, string Type, string Severity, string Title, string Detail, long? AlertId);

    public sealed record Summary(
        string Kind, string Id, int AlertCount, int OpenAlertCount, int ActivityCount,
        DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen, Dictionary<string, int> AlertsBySeverity);

    public sealed record Profile(Summary Summary, IReadOnlyList<TimelineItem> Timeline, bool Found);

    /// <summary>Builds the profile. <paramref name="kind"/> is "user" or "device".</summary>
    public async Task<Profile> BuildAsync(string kind, string id, int maxItems, CancellationToken ct)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        id = (id ?? "").Trim();
        var isUser = kind != "device";

        var alerts = isUser
            ? await db.SecurityAlerts.AsNoTracking().Where(a => a.UserPrincipalName == id).ToListAsync(ct)
            : await db.SecurityAlerts.AsNoTracking().Where(a => a.DeviceName == id).ToListAsync(ct);

        var activities = isUser
            ? await db.AuditEvents.AsNoTracking().Where(e => e.ActorUpn == id || e.TargetName == id).ToListAsync(ct)
            : await db.AuditEvents.AsNoTracking().Where(e => e.TargetName == id).ToListAsync(ct);

        var timeline = new List<TimelineItem>(alerts.Count + activities.Count);
        foreach (var a in alerts)
        {
            var detail = $"{a.Service} · {(a.IsResolved ? "resolved" : "active")}";
            timeline.Add(new TimelineItem(a.DetectedAt, "alert", a.Severity.ToString().ToLowerInvariant(), a.Title, detail, a.Id));
        }
        foreach (var e in activities)
        {
            // Frame the activity from this entity's point of view: did they do it, or was it done to them?
            var role = string.Equals(e.ActorUpn, id, StringComparison.OrdinalIgnoreCase) ? "by this user"
                : string.Equals(e.TargetName, id, StringComparison.OrdinalIgnoreCase) ? "targeted this entity"
                : "";
            var actor = e.ActorUpn ?? e.ActorApp;
            var detailParts = new[] { e.Category, e.Result, role, actor is null ? null : $"actor: {actor}" }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var severity = string.Equals(e.Result, "failure", StringComparison.OrdinalIgnoreCase) ? "medium" : "informational";
            timeline.Add(new TimelineItem(e.OccurredAt, "activity", severity, e.Activity, string.Join(" · ", detailParts), null));
        }

        timeline.Sort((x, y) => y.At.CompareTo(x.At));
        var capped = timeline.Count > maxItems ? timeline.Take(maxItems).ToList() : timeline;

        var summary = new Summary(
            Kind: isUser ? "user" : "device",
            Id: id,
            AlertCount: alerts.Count,
            OpenAlertCount: alerts.Count(a => !a.IsResolved),
            ActivityCount: activities.Count,
            FirstSeen: timeline.Count > 0 ? timeline.Min(t => t.At) : null,
            LastSeen: timeline.Count > 0 ? timeline.Max(t => t.At) : null,
            AlertsBySeverity: alerts.GroupBy(a => a.Severity.ToString().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Count()));

        return new Profile(summary, capped, alerts.Count > 0 || activities.Count > 0);
    }
}
