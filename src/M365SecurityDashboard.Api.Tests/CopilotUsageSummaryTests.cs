using System.Text.Json;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The Copilot page's numbers: who is active (measured from the report's refresh date),
/// which apps they use, whether the tenant hides names, and how many paid seats do nothing.
/// Report rows follow Microsoft's documented beta schema.
/// </summary>
public sealed class CopilotUsageSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<JsonElement> Rows(params object[] rows) =>
        rows.Select(r => JsonSerializer.SerializeToElement(r)).ToList();

    private static object Row(string upn, string name, string last, string teams = "", string word = "", string chat = "", string refresh = "2026-10-17") => new
    {
        reportRefreshDate = refresh, userPrincipalName = upn, displayName = name, lastActivityDate = last,
        microsoftTeamsCopilotLastActivityDate = teams, wordCopilotLastActivityDate = word, copilotChatLastActivityDate = chat,
        excelCopilotLastActivityDate = "", powerPointCopilotLastActivityDate = "", outlookCopilotLastActivityDate = "",
        oneNoteCopilotLastActivityDate = "", loopCopilotLastActivityDate = "",
    };

    private static object SkuRow(string part, int purchased, int consumed) =>
        new { skuPartNumber = part, consumedUnits = consumed, prepaidUnits = new { enabled = purchased } };

    [Fact]
    public void Users_are_active_inactive_or_never_used_counting_from_the_report_refresh_date()
    {
        var s = CopilotUsageSummary.Build(Rows(
            Row("priya@contoso.com", "Priya Shah", "2026-10-16", teams: "2026-10-16", word: "2026-10-01"),
            Row("lena@contoso.com", "Lena Novak", "2026-09-10", teams: "2026-09-10"),        // 37 days before refresh
            Row("marco@contoso.com", "Marco Rossi", "")), [], Now);

        Assert.Equal("2026-10-17", s.ReportRefreshDate);
        Assert.Equal(3, s.LicensedUsers);
        Assert.Equal(1, s.Active);
        Assert.Equal(1, s.Inactive);
        Assert.Equal(1, s.NeverUsed);
        Assert.Equal(["Teams", "Word"], s.Users.Single(u => u.Upn == "priya@contoso.com").Apps);
        Assert.Empty(s.Users.Single(u => u.Upn == "lena@contoso.com").Apps); // used Teams, but not within 30 days
        Assert.Equal(CopilotUsageSummary.StatusNeverUsed, s.Users.Single(u => u.Upn == "marco@contoso.com").Status);
    }

    [Fact]
    public void Each_app_counts_the_users_active_in_it()
    {
        var s = CopilotUsageSummary.Build(Rows(
            Row("a@contoso.com", "A", "2026-10-16", teams: "2026-10-16", chat: "2026-10-15"),
            Row("b@contoso.com", "B", "2026-10-10", teams: "2026-10-10")), [], Now);

        Assert.Equal(2, s.Apps.Single(a => a.Name == "Teams").Active);
        Assert.Equal(1, s.Apps.Single(a => a.Name == "Copilot Chat").Active);
        Assert.Equal(0, s.Apps.Single(a => a.Name == "Excel").Active);
        Assert.Equal(CopilotUsageSummary.AppColumns.Length, s.Apps.Count);
    }

    [Fact]
    public void A_report_with_hidden_names_is_flagged()
    {
        var s = CopilotUsageSummary.Build(Rows(Row("DC8C64D6EC3A3AA17481D7E5EB5B68A6", "C65E8D8AFA0DAB9639EDFAAEA94AFE66", "2026-10-16")), [], Now);
        Assert.True(s.NamesConcealed);
        Assert.False(CopilotUsageSummary.Build(Rows(Row("priya@contoso.com", "Priya", "2026-10-16")), [], Now).NamesConcealed);
    }

    [Fact]
    public void Seats_count_Copilot_licences_only_and_unused_is_bought_minus_assigned()
    {
        var s = CopilotUsageSummary.Build([], Rows(
            SkuRow("Microsoft_365_Copilot", 60, 48),
            SkuRow("Microsoft_Copilot_Studio_Viral", 10000, 3),   // building agents, not a user seat
            SkuRow("SPE_E5", 100, 90)), Now);

        Assert.Equal(60, s.Seats);
        Assert.Equal(48, s.AssignedSeats);
        Assert.Equal(12, s.UnusedSeats);
        Assert.Equal(["Microsoft_365_Copilot"], s.Skus.Select(k => k.PartNumber));
    }

    [Fact]
    public void The_report_period_is_one_Microsofts_v2_reports_accept_and_matches_the_active_window()
    {
        // Graph rejected 'D30' on a live tenant: "The period value 'RL30' is not supported with version 'v2'. Use 'RL28' instead."
        Assert.Contains(CopilotUsageSummary.ReportPeriod, new[] { "D7", "D28", "D90", "D180" });
        Assert.Equal(CopilotUsageSummary.ReportPeriod, $"D{CopilotUsageSummary.ActiveDays}");
    }

    [Fact]
    public void Microsofts_own_message_is_lifted_from_a_report_error_without_the_raw_response()
    {
        const string raw = """400 BadRequest: {"error":{"code":"UnknownError","message":"{\"error\":{\"code\":\"badRequest\",\"message\":\"The period value 'RL30' is not supported with version 'v2'. Use 'RL28' instead.\"}}","innerError":{"date":"2026-10-19T15:46:48","request-id":"35d9fc93-fc17-4c44-8fdf-712ea0f5b190"}}}""";

        Assert.Equal("The period value 'RL30' is not supported with version 'v2'. Use 'RL28' instead.", GraphErrorHint.GraphMessageOrNull(raw));
        Assert.Null(GraphErrorHint.GraphMessageOrNull("Connection refused"));
        Assert.Null(GraphErrorHint.DescribeOrNull(raw)); // not one we recognise, so the endpoint falls back to Microsoft's sentence
    }

    [Fact]
    public void A_tenant_without_Copilot_licences_has_no_seat_numbers_rather_than_zeros()
    {
        var s = CopilotUsageSummary.Build([], Rows(SkuRow("SPE_E5", 100, 90)), Now);
        Assert.Null(s.Seats);
        Assert.Null(s.UnusedSeats);
        Assert.Equal(0, s.LicensedUsers);
        Assert.Null(s.ReportRefreshDate);
    }
}
