using System.Text.Json;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The Conditional Access table summarised only users and built-in controls: a policy scoped to groups
/// or roles read "0 users", a user-action policy "0 apps", and one requiring an authentication strength
/// "None" — an MFA policy for two groups showed as "0 users · None".
/// </summary>
public class ConditionalAccessSummaryTests
{
    private static CaPolicySummary Of(string json) => ConditionalAccessSummary.Summarize(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Groups_roles_exclusions_office365_auth_strength_and_session_controls_are_named()
    {
        var s = Of("""
            {"displayName":"MFA outside trusted locations","state":"enabled",
             "conditions":{"users":{"includeUsers":[],"includeGroups":["g1","g2"],"includeRoles":["r1"],"excludeUsers":["bg"],"excludeGroups":["x"]},
                           "applications":{"includeApplications":["Office365"]}},
             "grantControls":{"builtInControls":[],"authenticationStrength":{"displayName":"Phishing-resistant MFA"}},
             "sessionControls":{"signInFrequency":{"isEnabled":true},"persistentBrowser":{"isEnabled":false}}}
            """);
        Assert.Equal("2 groups, 1 role", s.InclUsers);
        Assert.Equal("2 excluded", s.ExclUsers);
        Assert.Equal("Office 365", s.Apps);
        Assert.Equal(new[] { "Auth strength: Phishing-resistant MFA", "Sign-in frequency" }, s.Controls);
    }

    [Fact]
    public void All_users_and_a_user_action_with_built_in_controls()
    {
        var s = Of("""
            {"displayName":"Security info registration","state":"enabled",
             "conditions":{"users":{"includeUsers":["All"],"includeGuestsOrExternalUsers":{"guestOrExternalUserTypes":"b2bCollaborationGuest"}},
                           "applications":{"includeApplications":[],"includeUserActions":["urn:user:registersecurityinfo"]}},
             "grantControls":{"builtInControls":["mfa","compliantDevice"]}}
            """);
        Assert.Equal("All Users", s.InclUsers);
        Assert.Equal("Register security info", s.Apps);
        Assert.Equal(new[] { "mfa", "compliantDevice" }, s.Controls);
    }

    [Fact]
    public void Guests_only_and_an_empty_policy()
    {
        Assert.Equal("guests", Of("""{"conditions":{"users":{"includeUsers":["GuestsOrExternalUsers"]},"applications":{"includeApplications":["All"]}}}""").InclUsers);
        var empty = Of("""{"displayName":"Empty","state":"disabled","conditions":{"users":{"includeUsers":[]},"applications":{"includeApplications":[]}}}""");
        Assert.Equal("No users", empty.InclUsers);
        Assert.Equal("No apps", empty.Apps);
        Assert.Empty(empty.Controls);
    }

    [Fact]
    public void Resilience_defaults_off_is_read_from_a_boolean()
    {
        // Graph sends disableResilienceDefaults as true or false, not as an object like the other session controls.
        Assert.Equal(new[] { "Resilience defaults off" }, Of("""{"sessionControls":{"disableResilienceDefaults":true}}""").Controls);
        Assert.Empty(Of("""{"sessionControls":{"disableResilienceDefaults":false}}""").Controls);
    }

    [Fact]
    public void A_single_user_and_named_apps_keep_the_previous_wording()
    {
        var s = Of("""{"conditions":{"users":{"includeUsers":["u1"]},"applications":{"includeApplications":["a1","a2"]}},"grantControls":{"builtInControls":["block"]}}""");
        Assert.Equal("1 user", s.InclUsers);
        Assert.Equal("2 apps", s.Apps);
        Assert.Equal("None", s.ExclUsers);
    }
}
