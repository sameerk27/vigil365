using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// An analyst note attached to an alert — the "what did we find" record the
/// triage loop needs. Covers both alert kinds via (TargetKind, TargetId):
/// "security" + SecurityAlert.Id, or "policy" + TriggeredAlert.Id. Append-only.
/// </summary>
public sealed class AlertNote
{
    public long Id { get; set; }

    /// <summary>"security" (collected M365 alert) or "policy" (triggered policy alert).</summary>
    [MaxLength(20)]
    public string TargetKind { get; set; } = "";

    /// <summary>String form of the target's primary key (long or Guid).</summary>
    [MaxLength(64)]
    public string TargetId { get; set; } = "";

    /// <summary>Email of the analyst who wrote the note (from the validated token).</summary>
    [MaxLength(320)]
    public string Author { get; set; } = "";

    [MaxLength(2000)]
    public string Text { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
