using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

public sealed class SecurityAlert
{
    public long Id { get; set; }

    [MaxLength(256)]
    public string? ExternalId { get; set; }

    [MaxLength(120)]
    public string AlertType { get; set; } = "";

    public M365ServiceArea Service { get; set; }
    public AlertSeverity Severity { get; set; }

    [MaxLength(512)]
    public string Title { get; set; } = "";

    [MaxLength(4000)]
    public string? Description { get; set; }

    [MaxLength(320)]
    public string? UserPrincipalName { get; set; }

    [MaxLength(256)]
    public string? DeviceName { get; set; }

    [MaxLength(2048)]
    public string? PortalUrl { get; set; }

    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public bool IsResolved { get; set; }
    public string RawJson { get; set; } = "{}";
}
