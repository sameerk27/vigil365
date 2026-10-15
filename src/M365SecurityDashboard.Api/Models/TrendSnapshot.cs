using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// A point-in-time snapshot of key security posture metrics. 
/// Captured at the end of each collection cycle for historical trend analysis.
/// </summary>
public sealed class TrendSnapshot : ITenantScoped
{
    /// <summary>Owning tenant. Stamped from the tenant context on insert; see ITenantScoped.</summary>
    public Guid TenantId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    public int RiskyUsersCount { get; set; }
    
    public double MfaCoveragePct { get; set; }
    
    public int NonCompliantDevicesCount { get; set; }
    
    public int CriticalAlertsCount { get; set; }
    
    public int HighAlertsCount { get; set; }
    
    public double SecureScorePct { get; set; }

    public int ComplianceIssuesCount { get; set; }
}
