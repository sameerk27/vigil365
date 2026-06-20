using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Audit record of an outbound notification attempt (Teams / email / webhook).
/// </summary>
public sealed class NotificationLog
{
    public long Id { get; set; }

    public Guid TriggeredAlertId { get; set; }

    [MaxLength(200)]
    public string PolicyName { get; set; } = "";

    /// <summary>teams | email | webhook</summary>
    [MaxLength(20)]
    public string Channel { get; set; } = "";

    [MaxLength(320)]
    public string? Target { get; set; }

    public bool Success { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}
