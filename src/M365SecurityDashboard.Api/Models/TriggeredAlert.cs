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
}
