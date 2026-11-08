using System.Net;
using M365SecurityDashboard.Api.Services.Posture;
using static M365SecurityDashboard.Api.Tests.PostureFixtures;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The client-presentable HTML report is handed to people outside the MSP, so everything that
/// came from a tenant (organisation name, policy names inside findings, the brand colour an MSP
/// typed) must arrive as text, never as markup.
/// </summary>
public sealed class PostureReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tenant_values_are_encoded_as_text()
    {
        // A Conditional Access policy name is set by the tenant's admins and reaches the report in a finding.
        var p = Policy("<img src=x onerror=alert(1)>");
        p["state"] = "disabled";
        var result = Evaluate(Snapshot([p]));

        var html = PostureReport.Html(result, "<script>alert('client')</script>", "Contoso & Partners", null, Now);

        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("Contoso &amp; Partners", html);
    }

    [Theory]
    [InlineData("#0f766e", "#0f766e")]
    [InlineData("red;}</style><script>alert(1)</script>", PostureReport.DefaultAccent)]
    [InlineData("#12345", PostureReport.DefaultAccent)]
    [InlineData(null, PostureReport.DefaultAccent)]
    public void Only_a_six_digit_hex_brand_colour_is_used(string? accent, string expected)
    {
        var html = PostureReport.Html(Evaluate(Snapshot([Policy()])), "Contoso", "Vigil365", accent, Now);

        Assert.Contains($"border-bottom:3px solid {expected}", html);
        Assert.DoesNotContain("</style><script>", html);
    }

    [Fact]
    public void Top_actions_show_failures_before_unused_features_and_at_most_five()
    {
        // E5 with nothing set up: several Fails and several "paid for, not used".
        var s = Snapshot([LegacyBlock()], false, E5) with
        {
            IntuneCompliancePolicies = Reading<int>.Ok(0),
            SimulationsLast90Days = Reading<int>.Ok(0),
            SharingCapability = Reading<string>.Ok("externalUserAndGuestSharing"),
            MfaRegistration = Reading<(int, int)>.Ok((10, 100)),
        };
        var result = Evaluate(s);

        var html = PostureReport.Html(result, "Contoso", "Vigil365", null, Now);
        var actions = html[html.IndexOf("<h2>Top actions</h2>", StringComparison.Ordinal)..];
        actions = actions[..actions.IndexOf("</ol>", StringComparison.Ordinal)];

        var items = actions.Split("<li>").Skip(1).ToList();
        var statuses = items
            .Select(i => result.Findings.First(f => i.Contains($"<strong>{WebUtility.HtmlEncode(f.Check.Title)}.</strong>")).Status)
            .ToList();

        Assert.True(result.Findings.Count(f => f.Status is PostureStatus.Fail or PostureStatus.LicensedNotConfigured) > 5);
        Assert.Equal(5, items.Count);
        Assert.Equal(PostureStatus.Fail, statuses[0]);
        Assert.Equal(statuses.OrderBy(st => st == PostureStatus.Fail ? 0 : 1), statuses);
    }

    [Fact]
    public void Checks_that_could_not_be_read_are_listed_rather_than_dropped()
    {
        var html = PostureReport.Html(Evaluate(Snapshot(null)), "Contoso", "Vigil365", null, Now);

        Assert.Contains("<h2>Couldn't be checked</h2>", html);
        Assert.Contains("Couldn&#39;t read Conditional Access", html); // finding text is encoded, apostrophes included
    }

    [Fact]
    public void Secure_Score_appears_only_when_it_was_read()
    {
        var without = PostureReport.Html(Evaluate(Snapshot([])), "Contoso", "Vigil365", null, Now);
        var with = PostureReport.Html(Evaluate(Snapshot([]) with { SecureScore = Reading<(double, double)>.Ok((41.6, 80)) }), "Contoso", "Vigil365", null, Now);

        Assert.DoesNotContain("Microsoft Secure Score", without);
        Assert.Contains("<td>Microsoft Secure Score</td><td>42 of 80 points</td>", with);
    }
}
