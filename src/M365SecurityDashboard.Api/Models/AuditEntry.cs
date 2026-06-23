using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// An immutable record of a security-relevant action taken in Vigil365 — who did
/// what, to what, and when. Provides the admin/action audit trail expected by
/// SOC 2 / ISO 27001 logging controls. Append-only: rows are never updated.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Email/UPN of the user who performed the action (from the validated token).</summary>
    [MaxLength(320)]
    public string ActorEmail { get; set; } = "";

    /// <summary>Short machine action code, e.g. "user.add", "user.role_change", "alert.resolve".</summary>
    [MaxLength(60)]
    public string Action { get; set; } = "";

    /// <summary>The kind of thing acted on, e.g. "user", "alert", "settings".</summary>
    [MaxLength(40)]
    public string TargetType { get; set; } = "";

    /// <summary>Identifier of the target (email, alert id, etc.).</summary>
    [MaxLength(320)]
    public string? TargetId { get; set; }

    /// <summary>Human-readable summary of what changed, e.g. "role Viewer -> Admin".</summary>
    [MaxLength(500)]
    public string? Details { get; set; }
}
