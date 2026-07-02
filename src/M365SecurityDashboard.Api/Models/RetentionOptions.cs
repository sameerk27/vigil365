namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// How long each kind of collected/derived data is kept before the nightly
/// pruning job deletes it. Bound from the "Retention" config section.
/// A value of 0 (or negative) disables pruning for that data set.
/// </summary>
public sealed class RetentionOptions
{
    /// <summary>Resolved security alerts (open alerts are never pruned).</summary>
    public int ResolvedAlertsDays { get; set; } = 90;

    /// <summary>Resolved/auto-resolved triggered alerts (open ones are never pruned).</summary>
    public int TriggeredAlertsDays { get; set; } = 180;

    /// <summary>Notification delivery log rows.</summary>
    public int NotificationLogsDays { get; set; } = 90;

    /// <summary>Collection run history.</summary>
    public int CollectionRunsDays { get; set; } = 90;

    /// <summary>Trend snapshots (drives the Trends page — keep long by default).</summary>
    public int TrendSnapshotsDays { get; set; } = 365;

    /// <summary>Audit entries. Pruning removes the oldest rows; chain verification
    /// starts from the first remaining hashed entry, so pruning never "breaks" it.</summary>
    public int AuditEntriesDays { get; set; } = 365;
}
