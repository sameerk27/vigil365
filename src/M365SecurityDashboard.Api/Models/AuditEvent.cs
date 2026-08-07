using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// One tenant audit activity (Entra directory audit record), collected
/// incrementally each cycle. This is the raw material for activity-based
/// alert policies ("user added to privileged role", "app consent granted") —
/// alerting on WHAT HAPPENED rather than on metric counts.
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; set; }

    /// <summary>Graph record id — dedupe key for incremental collection.</summary>
    [MaxLength(256)]
    public string ExternalId { get; set; } = "";

    /// <summary>Feed the event came from. "directoryAudit" for now; the unified
    /// audit log (Exchange/SharePoint activities) is a later source.</summary>
    [MaxLength(40)]
    public string Source { get; set; } = "directoryAudit";

    /// <summary>Graph activityDisplayName, e.g. "Add member to role".</summary>
    [MaxLength(200)]
    public string Activity { get; set; } = "";

    /// <summary>Graph category, e.g. "RoleManagement", "ApplicationManagement".</summary>
    [MaxLength(80)]
    public string? Category { get; set; }

    /// <summary>UPN of the initiating user, if a user initiated it.</summary>
    [MaxLength(320)]
    public string? ActorUpn { get; set; }

    /// <summary>Display name of the initiating app/service, when not a user.</summary>
    [MaxLength(200)]
    public string? ActorApp { get; set; }

    /// <summary>First target resource display name / UPN.</summary>
    [MaxLength(320)]
    public string? TargetName { get; set; }

    /// <summary>"success" / "failure" (Graph result).</summary>
    [MaxLength(20)]
    public string? Result { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CollectedAt { get; set; }

    /// <summary>Full Graph record for the detail view.</summary>
    public string RawJson { get; set; } = "{}";
}
