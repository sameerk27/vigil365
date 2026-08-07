namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Represents a rule in the 20-Rule Enterprise Alerting Baseline catalog.
/// Compares active tenant monitoring against best practices.
/// </summary>
public sealed class AlertBaselineRule
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = ""; // identity, devices, email, infrastructure
    public string Severity { get; set; } = ""; // critical, high, medium, low
    public string Description { get; set; } = "";
    public bool IsActive { get; set; }
    public string RuleType { get; set; } = "Vigil365"; // "Vigil365" or "NativeM365"
    public string Metric { get; set; } = ""; // For Vigil365 rules
    public int DefaultThreshold { get; set; } = 1; // For Vigil365 rules
    public string NativePortalBlade { get; set; } = ""; // For NativeM365 rules
    public string NativePortalDeepLink { get; set; } = ""; // For NativeM365 rules
}

public sealed class AlertCoverageScorecard
{
    public int TotalRules { get; set; }
    public int ActiveRules { get; set; }
    public int CoveragePercentage { get; set; }
    public List<AlertBaselineRule> Rules { get; set; } = new();
}
