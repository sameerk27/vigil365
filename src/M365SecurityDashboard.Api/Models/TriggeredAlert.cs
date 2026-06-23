using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// A record created when an <see cref="AlertPolicy"/> condition is met.
/// </summary>
public sealed class TriggeredAlert
{
    public Guid Id { get; set; }

    public Guid PolicyId { get; set; }

    [MaxLength(200)]
    public string PolicyName { get; set; } = "";

    [MaxLength(20)]
    public string Severity { get; set; } = "medium";

    [MaxLength(40)]
    public string Category { get; set; } = "identity";

    [MaxLength(300)]
    public string Condition { get; set; } = "";

    public int MetricValue { get; set; }
    public int Threshold { get; set; }

    public DateTimeOffset TriggeredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>new | acknowledged | resolved</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "new";

    public DateTimeOffset? AcknowledgedAt { get; set; }

    [MaxLength(120)]
    public string? AcknowledgedBy { get; set; }

    /// <summary>Whether outbound notifications were dispatched for this alert.</summary>
    public bool Notified { get; set; }

    /// <summary>If set, the alert is silenced until this timestamp. Status remains "new" or "acknowledged".</summary>
    public DateTimeOffset? SnoozedUntil { get; set; }

    /// <summary>Identity of the actor who snoozed this alert. Placeholder string ("dashboard") until auth lands.</summary>
    [MaxLength(120)]
    public string? SnoozedBy { get; set; }

    /// <summary>Number of consecutive evaluation cycles the underlying metric has been below this alert's threshold. Reset on any above-threshold observation.</summary>
    public int BelowThresholdStreakCount { get; set; }

    /// <summary>When the evaluator last inspected this alert (used for diagnostics and the auto-resolve debounce).</summary>
    public DateTimeOffset? LastEvaluatedAt { get; set; }
}
