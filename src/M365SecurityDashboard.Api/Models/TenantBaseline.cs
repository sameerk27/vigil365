namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// A frozen snapshot of the tenant's posture metrics at a point in time.
/// Policies with a drift condition, and the Baseline tab, compare the latest
/// <see cref="TrendSnapshot"/> against these values so an alert means
/// "something changed" rather than "something crossed a fixed threshold".
///
/// Singleton row (Id = 1). The metric columns mirror <see cref="TrendSnapshot"/>
/// exactly — a capture copies the newest snapshot into this row, so the numbers
/// are always real collected data, never fabricated.
/// </summary>
public sealed class TenantBaseline
{
    public int Id { get; set; } = 1;

    /// <summary>When this baseline was captured. Null = never captured.</summary>
    public DateTimeOffset? CapturedAt { get; set; }

    /// <summary>Email of the admin who captured it (or "system").</summary>
    public string? CapturedBy { get; set; }

    public int RiskyUsersCount { get; set; }
    public double MfaCoveragePct { get; set; }
    public int NonCompliantDevicesCount { get; set; }
    public int CriticalAlertsCount { get; set; }
    public int HighAlertsCount { get; set; }
    public double SecureScorePct { get; set; }
    public int ComplianceIssuesCount { get; set; }
}
