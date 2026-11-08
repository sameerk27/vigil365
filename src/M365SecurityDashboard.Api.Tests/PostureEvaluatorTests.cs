using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using M365SecurityDashboard.Api.Services.Posture;
using Microsoft.Extensions.Logging.Abstractions;
using static M365SecurityDashboard.Api.Tests.PostureFixtures;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Licence posture's rules: a pass must mean every sign-in is covered, a read that failed must
/// never be reported as a setting being off, and a feature no owned licence includes is never
/// judged as configured or not.
/// </summary>
public sealed class PostureEvaluatorTests
{
    private const string MfaPolicyCheck = "PST-AAD-001";
    private const string LegacyCheck = "PST-AAD-002";
    private const string MfaEnforcedCheck = "PST-AAD-003";
    private const string MfaRegistrationCheck = "PST-AAD-004";
    private const string HighRiskCheck = "PST-AAD-005";
    private const string PimCheck = "PST-AAD-006";
    private const string IntuneCheck = "PST-INT-001";
    private const string SharingCheck = "PST-SPO-001";
    private const string SafeLinksCheck = "PST-MDO-001";
    private const string SimulationCheck = "PST-MDO-003";

    /// <summary>Real ways an "All users" policy leaves sign-ins out.</summary>
    private static void Narrow(JsonObject p, string how)
    {
        switch (how)
        {
            case "one app": Apps(p)["includeApplications"] = Strings("00000002-0000-0ff1-ce00-000000000000"); break;
            case "an excluded app": Apps(p)["excludeApplications"] = Strings("d4ebce55-015a-49b5-a083-c84d1797ae8c"); break;
            case "MFA or a compliant device": GrantControls(p)["builtInControls"] = Strings("mfa", "compliantDevice"); break;
            case "risky sign-ins only": Conditions(p)["signInRiskLevels"] = Strings("medium", "high"); break;
            case "outside trusted locations":
                Conditions(p)["locations"] = new JsonObject { ["includeLocations"] = Strings("All"), ["excludeLocations"] = Strings("AllTrusted") };
                break;
            case "some platforms":
                Conditions(p)["platforms"] = new JsonObject { ["includePlatforms"] = Strings("android", "iOS"), ["excludePlatforms"] = Strings() };
                break;
            case "browser only": Conditions(p)["clientAppTypes"] = Strings("browser"); break;
            case "a device filter":
                Conditions(p)["devices"] = new JsonObject { ["deviceFilter"] = new JsonObject { ["mode"] = "exclude", ["rule"] = "device.isCompliant -eq True" } };
                break;
            case "an unknown condition": Conditions(p)["authenticationFlows"] = new JsonObject { ["transferMethods"] = "deviceCodeFlow" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(how), how, null);
        }
    }

    // ── Conditional Access: what passes ───────────────────────────────────────

    [Fact]
    public void An_enabled_MFA_policy_for_all_users_and_all_apps_passes_both_MFA_checks()
    {
        var s = Snapshot([Policy()]);

        Assert.Equal(PostureStatus.Pass, Check(s, MfaPolicyCheck).Status);
        var enforced = Check(s, MfaEnforcedCheck);
        Assert.Equal(PostureStatus.Pass, enforced.Status);
        Assert.Contains("Require MFA for all users", enforced.Detail);
    }

    [Fact]
    public void An_authentication_strength_counts_as_MFA()
    {
        var p = Policy();
        GrantControls(p)["builtInControls"] = Strings();
        GrantControls(p)["authenticationStrength"] = new JsonObject { ["id"] = "00000000-0000-0000-0000-000000000002", ["displayName"] = "Multifactor authentication" };

        Assert.Equal(PostureStatus.Pass, Check(Snapshot([p]), MfaPolicyCheck).Status);
    }

    [Fact]
    public void MFA_required_together_with_a_compliant_device_passes()
    {
        var p = Policy();
        GrantControls(p)["operator"] = "AND";
        GrantControls(p)["builtInControls"] = Strings("mfa", "compliantDevice");

        Assert.Equal(PostureStatus.Pass, Check(Snapshot([p]), MfaPolicyCheck).Status);
    }

    [Fact]
    public void Conditions_set_to_cover_everything_still_pass()
    {
        var p = Policy();
        Conditions(p)["locations"] = new JsonObject { ["includeLocations"] = Strings("All"), ["excludeLocations"] = Strings() };
        Conditions(p)["platforms"] = new JsonObject { ["includePlatforms"] = Strings("all"), ["excludePlatforms"] = Strings() };
        Conditions(p)["clientAppTypes"] = Strings("browser", "mobileAppsAndDesktopClients");

        Assert.Equal(PostureStatus.Pass, Check(Snapshot([p]), MfaPolicyCheck).Status);
    }

    [Fact]
    public void Excluded_accounts_still_pass_but_are_listed_for_review()
    {
        var p = Policy();
        Users(p)["excludeUsers"] = Strings("aaaaaaaa-0000-0000-0000-000000000001", "aaaaaaaa-0000-0000-0000-000000000002");
        Users(p)["excludeGroups"] = Strings("bbbbbbbb-0000-0000-0000-000000000001");

        var f = Check(Snapshot([p]), MfaPolicyCheck);

        Assert.Equal(PostureStatus.Pass, f.Status);
        Assert.Contains("It excludes 2 users and 1 group; check each exclusion is deliberate.", f.Detail);
    }

    [Fact]
    public void A_full_policy_passes_alongside_a_narrower_one()
    {
        var exchangeOnly = Policy("MFA for Exchange");
        Narrow(exchangeOnly, "one app");

        Assert.Equal(PostureStatus.Pass, Check(Snapshot([exchangeOnly, Policy()]), MfaPolicyCheck).Status);
    }

    // ── Conditional Access: all users on paper, not every sign-in ─────────────

    [Theory]
    [InlineData("one app", "it doesn't cover all cloud apps")]
    [InlineData("an excluded app", "it excludes 1 cloud app")]
    [InlineData("MFA or a compliant device", "it accepts a compliant device instead of MFA")]
    [InlineData("risky sign-ins only", "it applies only to risky sign-ins")]
    [InlineData("outside trusted locations", "it depends on where the sign-in comes from")]
    [InlineData("some platforms", "it covers only some device platforms")]
    [InlineData("browser only", "it doesn't cover every client app")]
    [InlineData("a device filter", "it has a device filter")]
    [InlineData("an unknown condition", "it has an extra condition (authenticationFlows)")]
    public void An_all_users_MFA_policy_that_leaves_sign_ins_out_fails_and_says_why(string how, string why)
    {
        var p = Policy();
        Narrow(p, how);
        var s = Snapshot([p]);

        var policy = Check(s, MfaPolicyCheck);
        Assert.Equal(PostureStatus.Fail, policy.Status);
        Assert.Contains($"Enabled policy “Require MFA for all users” is for all users, but {why}.", policy.Detail);

        var enforced = Check(s, MfaEnforcedCheck);
        Assert.Equal(PostureStatus.Fail, enforced.Status);
        Assert.Contains(why, enforced.Detail);
    }

    [Fact]
    public void A_missing_grant_operator_with_another_control_is_treated_as_OR()
    {
        var p = Policy();
        GrantControls(p).Remove("operator");
        GrantControls(p)["builtInControls"] = Strings("mfa", "compliantDevice");

        Assert.Equal(PostureStatus.Fail, Check(Snapshot([p]), MfaPolicyCheck).Status);
    }

    [Fact]
    public void A_covering_policy_in_report_only_mode_fails_and_says_so()
    {
        var p = Policy();
        p["state"] = "enabledForReportingButNotEnforced";

        var f = Check(Snapshot([p]), MfaPolicyCheck);

        Assert.Equal(PostureStatus.Fail, f.Status);
        Assert.Equal("Policy “Require MFA for all users” requires MFA for all users, but it is in report-only mode.", f.Detail);
    }

    // ── Legacy authentication and risk ────────────────────────────────────────

    [Fact]
    public void A_legacy_block_for_all_apps_passes_and_one_for_some_apps_fails()
    {
        Assert.Equal(PostureStatus.Pass, Check(Snapshot([LegacyBlock()]), LegacyCheck).Status);

        var some = LegacyBlock();
        Narrow(some, "one app");
        var f = Check(Snapshot([some]), LegacyCheck);
        Assert.Equal(PostureStatus.Fail, f.Status);
        Assert.Contains("it doesn't cover all cloud apps", f.Detail);
    }

    [Fact]
    public void Separate_blocks_for_high_user_risk_and_high_sign_in_risk_pass()
    {
        var s = Snapshot([RiskBlock("Block risky users", "userRiskLevels"), RiskBlock("Block risky sign-ins", "signInRiskLevels")]);

        Assert.Equal(PostureStatus.Pass, Check(s, HighRiskCheck).Status);
    }

    [Fact]
    public void One_policy_with_both_risk_conditions_only_blocks_when_both_are_high()
    {
        // Microsoft needs every condition to match, so this blocks a high-risk user only on a high-risk sign-in.
        var f = Check(Snapshot([RiskBlock("Block risk", "userRiskLevels", "signInRiskLevels")]), HighRiskCheck);

        Assert.Equal(PostureStatus.Fail, f.Status);
        Assert.Contains("it only blocks when the sign-in is risky too", f.Detail);
        Assert.Contains("it only blocks when the user is risky too", f.Detail);
    }

    [Fact]
    public void No_risk_policy_is_licensed_but_not_configured()
    {
        Assert.Equal(PostureStatus.LicensedNotConfigured, Check(Snapshot([Policy()]), HighRiskCheck).Status);
    }

    // ── Reads that failed ─────────────────────────────────────────────────────

    [Fact]
    public void Unreadable_Security_Defaults_and_no_covering_policy_is_unknown_not_off()
    {
        var f = Check(Snapshot([LegacyBlock()], securityDefaults: null), MfaEnforcedCheck);

        Assert.Equal(PostureStatus.Unknown, f.Status);
        Assert.StartsWith("Couldn't read Security Defaults: ", f.Detail);
        Assert.DoesNotContain("Security Defaults are off", f.Detail);
    }

    [Fact]
    public void Unreadable_Security_Defaults_with_a_covering_policy_passes()
    {
        Assert.Equal(PostureStatus.Pass, Check(Snapshot([Policy()], securityDefaults: null), MfaEnforcedCheck).Status);
    }

    [Fact]
    public void Unreadable_Conditional_Access_in_a_licensed_tenant_is_unknown_not_missing()
    {
        var f = Check(Snapshot(null, securityDefaults: false), MfaEnforcedCheck);

        Assert.Equal(PostureStatus.Unknown, f.Status);
        Assert.Contains("Conditional Access couldn't be read", f.Detail);
        Assert.Equal(PostureStatus.Unknown, Check(Snapshot(null), MfaPolicyCheck).Status);
    }

    [Fact]
    public void Without_a_Conditional_Access_licence_only_Security_Defaults_count()
    {
        var f = Check(Snapshot(null, false, ExchangeOnly), MfaEnforcedCheck);

        Assert.Equal(PostureStatus.Fail, f.Status);
        Assert.Equal("Security Defaults are off, and no licence in this tenant includes Conditional Access.", f.Detail);
    }

    [Fact]
    public void Both_reads_failing_names_both()
    {
        var f = Check(Snapshot(null, securityDefaults: null), MfaEnforcedCheck);

        Assert.Equal(PostureStatus.Unknown, f.Status);
        Assert.Contains("Couldn't read Security Defaults", f.Detail);
        Assert.Contains("Conditional Access couldn't be read either", f.Detail);
    }

    [Fact]
    public void Security_Defaults_on_passes_with_no_policies()
    {
        Assert.Equal(PostureStatus.Pass, Check(Snapshot([], securityDefaults: true), MfaEnforcedCheck).Status);
    }

    [Fact]
    public void Unreadable_licences_make_every_check_unknown()
    {
        var s = Snapshot([Policy()]) with { Skus = Reading<IReadOnlyList<OwnedSku>>.Failed("Permission denied.") };

        var result = Evaluate(s);

        Assert.All(result.Findings, f => Assert.Equal(PostureStatus.Unknown, f.Status));
        Assert.NotNull(result.Error);
    }

    // ── Licences ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_feature_no_owned_licence_includes_is_not_licensed_and_names_the_add_on_first()
    {
        var result = Evaluate(Snapshot([Policy()], false, ExchangeOnly));
        var ca = result.Findings.Single(f => f.Check.Id == MfaPolicyCheck);

        Assert.Equal(PostureStatus.NotLicensed, ca.Status);
        Assert.Null(ca.LicensedBy);
        Assert.Equal("Microsoft Entra ID P2", ca.UnlockedBy[0]); // the add-on before the suites that include it
        Assert.Contains("Microsoft 365 Business Premium", ca.UnlockedBy);
        Assert.Contains(result.Upgrades, g => g.Findings.Any(f => f.Check.Id == MfaPolicyCheck));
        Assert.Equal(result.Summary.NotLicensed, result.Findings.Count(f => f.Status == PostureStatus.NotLicensed));
    }

    [Fact]
    public void A_licence_with_no_enabled_seats_is_not_owned()
    {
        var lapsed = EntraP2 with { Enabled = 0 };

        Assert.Equal(PostureStatus.NotLicensed, Check(Snapshot([Policy()], false, lapsed), MfaPolicyCheck).Status);
    }

    [Fact]
    public void Free_checks_are_evaluated_for_every_tenant()
    {
        var f = Check(Snapshot([], true, ExchangeOnly), MfaEnforcedCheck);

        Assert.Equal(PostureStatus.Pass, f.Status);
        Assert.Equal("Every Microsoft 365 tenant", f.LicensedBy);
    }

    [Fact]
    public void Included_features_list_only_paid_features_an_owned_licence_includes()
    {
        var included = Evaluate(Snapshot([], false, EntraP2)).IncludedFeatures;

        Assert.Contains("Microsoft Entra ID P1 (Conditional Access)", included);
        Assert.DoesNotContain("Every Microsoft 365 tenant", included);
        Assert.DoesNotContain(included, name => name.Contains("Intune"));
    }

    // ── Other rules ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(95, 100, PostureStatus.Pass)]
    [InlineData(949, 1000, PostureStatus.Fail)] // 94.9% rounds down: a near miss is still a miss
    [InlineData(0, 0, PostureStatus.Unknown)]
    public void MFA_registration_compares_the_rounded_down_share_with_the_threshold(int registered, int total, PostureStatus expected)
    {
        var s = Snapshot([]) with { MfaRegistration = Reading<(int, int)>.Ok((registered, total)) };

        Assert.Equal(expected, Check(s, MfaRegistrationCheck).Status);
    }

    [Fact]
    public void MFA_registration_below_target_says_how_many_are_missing()
    {
        var s = Snapshot([]) with { MfaRegistration = Reading<(int, int)>.Ok((949, 1000)) };

        Assert.Equal("94% of member users are registered for MFA (target 95%); 51 aren't.", Check(s, MfaRegistrationCheck).Detail);
    }

    [Theory]
    [InlineData("disabled", PostureStatus.Pass)]
    [InlineData("existingExternalUserSharingOnly", PostureStatus.Pass)]
    [InlineData("externalUserSharingOnly", PostureStatus.Fail)]
    [InlineData("externalUserAndGuestSharing", PostureStatus.Fail)]
    [InlineData("aLevelMicrosoftAddsLater", PostureStatus.Unknown)]
    public void SharePoint_sharing_passes_only_when_limited_to_the_organisation_or_existing_guests(string level, PostureStatus expected)
    {
        var s = Snapshot([], false, E5) with { SharingCapability = Reading<string>.Ok(level) };

        Assert.Equal(expected, Check(s, SharingCheck).Status);
    }

    [Fact]
    public void Counted_settings_pass_when_present_are_unused_when_absent_and_unknown_when_unread()
    {
        var s = Snapshot([], false, E5) with { IntuneCompliancePolicies = Reading<int>.Ok(3), SimulationsLast90Days = Reading<int>.Ok(0) };

        Assert.Equal(PostureStatus.Pass, Check(s, IntuneCheck).Status);
        Assert.Equal(PostureStatus.LicensedNotConfigured, Check(s, SimulationCheck).Status);
        Assert.Equal(PostureStatus.Unknown, Check(Snapshot([], false, E5), IntuneCheck).Status);
    }

    [Fact]
    public void Exchange_checks_are_unknown_until_Exchange_PowerShell_is_connected()
    {
        var f = Check(Snapshot([], false, E5), SafeLinksCheck);

        Assert.Equal(PostureStatus.Unknown, f.Status);
        Assert.Equal(PostureEvaluator.ExchangeNotConnected, f.Detail);
        Assert.Equal("Exchange Online PowerShell", f.ReadFrom);
    }

    [Fact]
    public void PIM_says_which_permission_it_would_need()
    {
        var f = Check(Snapshot([], false, E5), PimCheck);

        Assert.Equal(PostureStatus.Unknown, f.Status);
        Assert.Contains("RoleManagement.Read.Directory", f.Detail);
    }

    [Fact]
    public void SCuBA_counts_only_mapped_checks_and_Secure_Score_appears_only_when_read()
    {
        var unread = Evaluate(Snapshot([Policy(), LegacyBlock()], false, E5));
        Assert.Equal(PostureCatalog.Default.Checks.Count(c => c.Baselines.Scuba.Count > 0), unread.Scuba.Total);
        Assert.True(unread.Scuba.Pass >= 2);
        Assert.Null(unread.SecureScore);

        var read = Evaluate(Snapshot([], false, E5) with { SecureScore = Reading<(double, double)>.Ok((41.5, 80)) });
        Assert.Equal((41.5, 80.0), read.SecureScore);
    }

    // ── Collector ─────────────────────────────────────────────────────────────

    private sealed class FixedToken : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("token", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    private sealed class FakeGraph(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(Uri.UnescapeDataString(request.RequestUri!.ToString())));
    }

    private const string GraphBase = "https://graph.test";

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static PostureCollector Collector(FakeGraph graph) => new(
        new GraphApiClient(new HttpClient(graph),
            new GraphOptions { TenantId = "11111111-1111-1111-1111-111111111111", ClientId = "c", ClientSecret = "s", BaseUrl = GraphBase },
            new FixedToken(), new GraphMetrics()),
        NullLogger<PostureCollector>.Instance);

    [Fact]
    public async Task A_policy_list_or_MFA_report_missing_a_page_is_unreadable_not_short()
    {
        var graph = new FakeGraph(url =>
            url.Contains("skiptoken") ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : url.Contains("/identity/conditionalAccess/policies")
                ? Json($$"""{"value":[{{Policy("Narrow").ToJsonString()}}],"@odata.nextLink":"{{GraphBase}}/v1.0/identity/conditionalAccess/policies?$skiptoken=2"}""")
            : url.Contains("userRegistrationDetails")
                ? Json($$"""{"value":[{"isMfaRegistered":true,"userType":"member"}],"@odata.nextLink":"{{GraphBase}}/v1.0/reports/authenticationMethods/userRegistrationDetails?$skiptoken=2"}""")
            : Json("""{"value":[]}"""));

        var snapshot = await Collector(graph).CollectAsync(PostureCatalog.Default, CancellationToken.None);

        Assert.False(snapshot.ConditionalAccess.Read);
        Assert.Equal("Only some Conditional Access policies could be read; try again later.", snapshot.ConditionalAccess.Error);
        Assert.Equal("The MFA registration report could only be read in part; try again later.", snapshot.MfaRegistration.Error);
    }

    [Fact]
    public async Task Turned_off_service_plans_and_suspended_licences_are_not_owned()
    {
        var graph = new FakeGraph(url => url.Contains("subscribedSkus")
            ? Json("""
                {"value":[
                  {"skuPartNumber":"SPE_E5","capabilityStatus":"Enabled","consumedUnits":4,"prepaidUnits":{"enabled":10},
                   "servicePlans":[{"servicePlanName":"AAD_PREMIUM_P2","provisioningStatus":"Success"},
                                   {"servicePlanName":"INTUNE_A","provisioningStatus":"Disabled"}]},
                  {"skuPartNumber":"SPB","capabilityStatus":"Suspended","consumedUnits":0,"prepaidUnits":{"enabled":5},
                   "servicePlans":[{"servicePlanName":"INTUNE_A","provisioningStatus":"Success"}]}
                ]}
                """)
            : Json("""{"value":[]}"""));

        var snapshot = await Collector(graph).CollectAsync(PostureCatalog.Default, CancellationToken.None);

        var sku = Assert.Single(snapshot.Skus.Value!);
        Assert.Equal(("SPE_E5", "Microsoft 365 E5", 10, 4), (sku.PartNumber, sku.Name, sku.Enabled, sku.Consumed));
        Assert.Equal(["AAD_PREMIUM_P2"], sku.ServicePlans);
    }
}
