using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// A user-defined alert policy. Evaluated against collected data on every
/// collection cycle (server-side), so alerts fire even when no browser is open.
/// </summary>
public sealed class AlertPolicy
{
    public Guid Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    [MaxLength(40)]
    public string Category { get; set; } = "identity";

    [MaxLength(300)]
    public string Condition { get; set; } = "";

    /// <summary>
    /// "metric" — fires when a computed metric crosses <see cref="Threshold"/>.
    /// "activity" — fires when tenant audit events matching
    /// <see cref="ActivityPattern"/> occur ≥ Threshold times within
    /// <see cref="WindowMinutes"/>. Activity policies alert on WHAT HAPPENED
    /// (role added, app consent granted), not on state counts.
    /// </summary>
    [MaxLength(20)]
    public string Kind { get; set; } = "metric";

    /// <summary>Metric key the engine watches, e.g. "criticalAlertCount". (Kind=metric)</summary>
    [MaxLength(60)]
    public string Metric { get; set; } = "";

    /// <summary>Activity name to match, * as wildcard — e.g. "Add member to role",
    /// "*conditional access policy". (Kind=activity)</summary>
    [MaxLength(200)]
    public string? ActivityPattern { get; set; }

    /// <summary>Sliding window for activity matching. (Kind=activity)</summary>
    public int WindowMinutes { get; set; } = 60;

    public int Threshold { get; set; }

    [MaxLength(20)]
    public string Severity { get; set; } = "medium";

    /// <summary>Optional email recipient for this policy (overrides global default).</summary>
    [MaxLength(320)]
    public string? NotifyEmail { get; set; }

    /// <summary>Suppress re-notification for this many minutes after a trigger.</summary>
    public int SuppressionMinutes { get; set; } = 60;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastTriggered { get; set; }
    public int TriggerCount { get; set; }
}
