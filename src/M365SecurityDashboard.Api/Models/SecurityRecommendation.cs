namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Represents an actionable security recommendation derived from live tenant telemetry or posture gaps.
/// Strictly provides read-only guidance and deep links into native Microsoft 365 / Azure portals.
/// </summary>
public sealed class SecurityRecommendation
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = ""; // Identity, Devices, Email & Collaboration, Data Protection, Infrastructure
    public string Title { get; set; } = "";
    public string Severity { get; set; } = ""; // critical, high, medium, low
    public int AffectedCount { get; set; }
    public string WhyItMatters { get; set; } = "";
    public List<string> RemediationSteps { get; set; } = new();
    public string PortalBladeName { get; set; } = "";
    public string PortalDeepLink { get; set; } = "";
}
