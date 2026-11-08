namespace M365SecurityDashboard.Api.Services.Common;

/// <summary>One ordering for alert severities. Higher is worse.</summary>
public static class SeverityScale
{
    public const int Informational = 0, Low = 1, Medium = 2, High = 3, Critical = 4;

    /// <param name="unknownAs">Notification filters pass <see cref="Low"/> so an unrecognised severity is
    /// still delivered at the lowest threshold rather than silently dropped.</param>
    public static int Rank(string? severity, int unknownAs = Informational) => (severity ?? "").ToLowerInvariant() switch
    {
        "critical" => Critical,
        "high" => High,
        "medium" => Medium,
        "low" => Low,
        "informational" => Informational,
        _ => unknownAs,
    };

    /// <summary>Hex colour (no #) for email and Teams cards, matching the SPA's severity palette.</summary>
    public static string HexColor(string? severity) => (severity ?? "").ToLowerInvariant() switch
    {
        "critical" => "dc2626",
        "high" => "ea580c",
        "medium" => "d97706",
        "low" => "2563eb",
        _ => "6b7280",
    };
}
