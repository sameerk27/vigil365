namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// Configuration for the server-side alerting engine. Bound from the
/// "Alerting" section of <c>appsettings.json</c>.
/// </summary>
public sealed class AlertingOptions
{
    /// <summary>
    /// Number of consecutive evaluation cycles the underlying metric must be
    /// below a triggered alert's threshold before the alert auto-resolves.
    /// With the default collection interval of 15 minutes and a value of 2,
    /// an alert auto-resolves ~30 minutes after the metric recovers.
    /// </summary>
    public int AutoResolveDebounceCycles { get; set; } = 2;
}
