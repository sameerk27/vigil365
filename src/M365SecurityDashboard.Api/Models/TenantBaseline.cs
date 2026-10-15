namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// A frozen copy of one tenant's newest TrendSnapshot, captured by an admin so
/// later snapshots can be measured against it (see BaselineDrift). One row per
/// tenant, keyed by the tenant: there is nothing else to key it on, and it
/// removes the fixed "Id = 1" singleton that could not coexist across tenants.
/// </summary>
public sealed class TenantBaseline : ITenantScoped
{
    public Guid TenantId { get; set; }

    /// <summary>When this baseline was captured. Null = never captured.</summary>
    public DateTimeOffset? CapturedAt { get; set; }

    /// <summary>Who captured it (email).</summary>
    public string? CapturedBy { get; set; }

    public int RiskyUsersCount { get; set; }
    public double MfaCoveragePct { get; set; }
    public int NonCompliantDevicesCount { get; set; }
    public int CriticalAlertsCount { get; set; }
    public int HighAlertsCount { get; set; }
    public double SecureScorePct { get; set; }
    public int ComplianceIssuesCount { get; set; }
}
