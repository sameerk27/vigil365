using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Where one tenant's Purview audit search stands, per feed ("powerPlatform",
/// "copilotStudio"). The search is asynchronous: one collection run submits it and a
/// later run reads its records, so the pending query and the window it covers live
/// here between runs. Also the status the Rules page shows ("Needs permission").
///
/// Tenant-scoped, one row per tenant and feed, created on demand.
/// </summary>
public sealed class PurviewAuditState : ITenantScoped
{
    public Guid TenantId { get; set; }

    [MaxLength(40)]
    public string Feed { get; set; } = "";

    /// <summary>The submitted audit search, while Microsoft is still running it.</summary>
    [MaxLength(100)]
    public string? PendingQueryId { get; set; }
    public DateTimeOffset? PendingFrom { get; set; }
    public DateTimeOffset? PendingTo { get; set; }
    public DateTimeOffset? PendingSubmittedAt { get; set; }

    /// <summary>End of the last window whose records were stored. The next search starts here, less an overlap for late records.</summary>
    public DateTimeOffset? Watermark { get; set; }

    public DateTimeOffset? LastSubmittedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>"NotStarted", "Waiting", "Ok", "NeedsPermission" or "Failed".</summary>
    [MaxLength(32)]
    public string Status { get; set; } = "NotStarted";

    /// <summary>A sentence for the operator, never raw Graph JSON.</summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>New events stored by the last completed search.</summary>
    public int LastEventCount { get; set; }
}
