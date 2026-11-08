namespace M365SecurityDashboard.Api.Services.Common;

/// <summary>Length guards for values headed into size-limited DB columns or messages.</summary>
public static class TextLimits
{
    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max)];

    public static string? TruncateOrNull(string? s, int max) => s is null ? null : Truncate(s, max);

    /// <summary>For display text: cuts one short and appends an ellipsis.</summary>
    public static string Ellipsize(string s, int max) =>
        s.Length <= max ? s : max < 1 ? "" : s[..(max - 1)] + "…";
}
