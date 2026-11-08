using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// One tenant's Microsoft 365 Copilot usage, from the Copilot usage report (Graph beta
/// getMicrosoft365CopilotUsageUserDetail, Reports.Read.All) and the licence list
/// (subscribedSkus). Answers "who has a seat, who uses it, and how many seats do
/// nothing". Pure, so every count is unit-tested.
/// </summary>
public static partial class CopilotUsageSummary
{
    /// <summary>The report's per-app columns, as Microsoft documents them, and the name shown for each.</summary>
    public static readonly (string Field, string App)[] AppColumns =
    [
        ("microsoftTeamsCopilotLastActivityDate", "Teams"),
        ("outlookCopilotLastActivityDate", "Outlook"),
        ("copilotChatLastActivityDate", "Copilot Chat"),
        ("wordCopilotLastActivityDate", "Word"),
        ("powerPointCopilotLastActivityDate", "PowerPoint"),
        ("excelCopilotLastActivityDate", "Excel"),
        ("oneNoteCopilotLastActivityDate", "OneNote"),
        ("loopCopilotLastActivityDate", "Loop"),
    ];

    /// <summary>
    /// The report period asked of Graph. Microsoft's v2 Copilot reports (MC1423101, 2026)
    /// accept 7, 28, 90 and 180 days and reject 30 ("The period value 'RL30' is not
    /// supported with version 'v2'"), so the page works in 28-day windows.
    /// </summary>
    public const string ReportPeriod = "D28";

    /// <summary>A user is active when Copilot was used within this many days of the report's refresh date. Matches <see cref="ReportPeriod"/>.</summary>
    public const int ActiveDays = 28;

    public const string StatusActive = "Active";
    public const string StatusInactive = "Inactive";
    public const string StatusNeverUsed = "Never used";

    public sealed record User(string Name, string? Upn, string? LastActivity, IReadOnlyList<string> Apps, string Status);
    public sealed record App(string Name, int Active);
    public sealed record Sku(string PartNumber, int Purchased, int Assigned);

    public sealed record Result(
        string? ReportRefreshDate,
        int LicensedUsers,
        int Active,
        int Inactive,
        int NeverUsed,
        int? Seats,
        int? AssignedSeats,
        int? UnusedSeats,
        IReadOnlyList<Sku> Skus,
        bool NamesConcealed,
        IReadOnlyList<App> Apps,
        IReadOnlyList<User> Users);

    // A concealed report replaces names with 32-character hex codes.
    [GeneratedRegex("^[0-9A-Fa-f]{32}$")]
    private static partial Regex ConcealedName();

    public static Result Build(IReadOnlyList<JsonElement> reportRows, IReadOnlyList<JsonElement> skus, DateTimeOffset now)
    {
        // Measured from the report's own refresh date: the report lags by a day or two,
        // and today's date would count a user who used Copilot yesterday as stale data.
        var refresh = reportRows.Select(r => Date(r, "reportRefreshDate")).FirstOrDefault(d => d is not null);
        var cutoff = (refresh ?? DateOnly.FromDateTime(now.UtcDateTime)).AddDays(-ActiveDays);

        var users = reportRows.Select(r =>
        {
            var upn = Str(r, "userPrincipalName");
            var last = Date(r, "lastActivityDate");
            var apps = AppColumns.Where(c => Date(r, c.Field) is { } d && d >= cutoff).Select(c => c.App).ToList();
            var status = last is null ? StatusNeverUsed : last >= cutoff ? StatusActive : StatusInactive;
            return new User(Str(r, "displayName") ?? upn ?? "(unknown)", upn, last?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), apps, status);
        })
        .OrderByDescending(u => u.LastActivity ?? "")
        .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

        var copilotSkus = skus
            .Select(s => new Sku(
                Str(s, "skuPartNumber") ?? "",
                s.TryGetProperty("prepaidUnits", out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0,
                s.TryGetProperty("consumedUnits", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0))
            .Where(s => IsCopilotSeat(s.PartNumber) && s.Purchased > 0)
            .ToList();
        int? seats = copilotSkus.Count > 0 ? copilotSkus.Sum(s => s.Purchased) : null;
        int? assigned = copilotSkus.Count > 0 ? copilotSkus.Sum(s => s.Assigned) : null;

        return new Result(
            refresh?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            users.Count,
            users.Count(u => u.Status == StatusActive),
            users.Count(u => u.Status == StatusInactive),
            users.Count(u => u.Status == StatusNeverUsed),
            seats,
            assigned,
            seats is { } s2 && assigned is { } a2 ? Math.Max(0, s2 - a2) : null,
            copilotSkus,
            users.Any(u => u.Upn is { } x && ConcealedName().IsMatch(x)),
            AppColumns.Select(c => new App(c.App, users.Count(u => u.Apps.Contains(c.App)))).ToList(),
            users);
    }

    /// <summary>
    /// A Microsoft 365 Copilot seat. Copilot Studio is capacity for building agents, not a
    /// user's Copilot seat. Confirm the part numbers on a licensed tenant (plan §6).
    /// </summary>
    internal static bool IsCopilotSeat(string partNumber) =>
        partNumber.Contains("COPILOT", StringComparison.OrdinalIgnoreCase)
        && !partNumber.Contains("STUDIO", StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static DateOnly? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateOnly.TryParse(s.Length >= 10 ? s[..10] : s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
