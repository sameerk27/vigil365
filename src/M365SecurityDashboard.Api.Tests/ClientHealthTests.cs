using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The states an MSP operator acts on (MSP review F2, F8, F9; feature 1). Pure logic, so
/// each rule is pinned directly: partial runs name their sources, consent that has not
/// replicated yet is not a failure, and a client's state never hides a deactivation.
/// </summary>
public sealed class ClientHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private static ClientStatusInput Input(bool active = true, bool configured = true, bool unreadable = false,
        string? status = null, DateTimeOffset? next = null)
        => new(active, configured, unreadable, status, next, Now);

    [Fact]
    public void No_failing_sources_means_completed_with_no_error()
    {
        var (status, error) = ClientHealth.OutcomeOf(0, null);
        Assert.Equal("Completed", status);
        Assert.Null(error);
    }

    [Fact]
    public void Failing_sources_make_the_client_partial_and_name_them()
    {
        var json = "[{\"source\":\"Secure Score\",\"error\":\"x\"},{\"source\":\"MFA registration\",\"error\":\"y\"}]";

        var (status, error) = ClientHealth.OutcomeOf(2, json);

        Assert.Equal(ClientHealth.Partial, status);
        Assert.Equal("2 source(s) failing: Secure Score, MFA registration. Open Collection Health for each error.", error);
    }

    [Fact]
    public void More_than_three_failing_sources_are_summarised_not_listed_in_full()
    {
        var json = "[{\"source\":\"A\",\"error\":\"\"},{\"source\":\"B\",\"error\":\"\"},{\"source\":\"C\",\"error\":\"\"},{\"source\":\"D\",\"error\":\"\"}]";

        var (_, error) = ClientHealth.OutcomeOf(4, json);

        Assert.Contains(": A, B, C and 1 more.", error);
    }

    [Fact]
    public void Unreadable_failure_details_still_report_the_count()
    {
        var (status, error) = ClientHealth.OutcomeOf(3, "not json");
        Assert.Equal(ClientHealth.Partial, status);
        Assert.StartsWith("3 source(s) failing.", error);
    }

    [Fact]
    public void Consent_pending_counts_only_when_every_failure_is_that()
    {
        var pending = "[{\"source\":\"Risky users\",\"error\":\"" + GraphErrorHint.NotYetProvisionedMessage + "\"}]";
        var mixed = "[{\"source\":\"Risky users\",\"error\":\"" + GraphErrorHint.NotYetProvisionedMessage + "\"}," +
                    "{\"source\":\"MFA\",\"error\":\"Permission denied\"}]";

        Assert.True(ClientHealth.AllFailuresAreConsentPending(pending));
        Assert.False(ClientHealth.AllFailuresAreConsentPending(mixed));
        Assert.False(ClientHealth.AllFailuresAreConsentPending(null));
        Assert.False(ClientHealth.AllFailuresAreConsentPending("[]"));
    }

    [Fact]
    public void A_deactivated_client_is_deactivated_whatever_else_is_true()
    {
        var s = ClientHealth.Of(Input(active: false, unreadable: true, status: "Failed"));
        Assert.Equal(ClientHealth.StateDeactivated, s.State);
    }

    [Fact]
    public void An_unreadable_secret_is_an_error_that_says_what_to_do()
    {
        var s = ClientHealth.Of(Input(configured: false, unreadable: true));
        Assert.Equal(ClientHealth.StateCredentialsUnreadable, s.State);
        Assert.Equal("error", s.Tone);
        Assert.Contains("re-enter", s.Label);
    }

    [Fact]
    public void No_credentials_yet_is_waiting_for_consent_not_failed()
    {
        var s = ClientHealth.Of(Input(configured: false, status: "Failed"));
        Assert.Equal(ClientHealth.StateWaitingForConsent, s.State);
    }

    [Fact]
    public void Microsoft_still_setting_up_the_app_is_waiting_not_failed()
    {
        var s = ClientHealth.Of(Input(status: ClientHealth.WaitingForConsent));
        Assert.Equal(ClientHealth.StateWaitingForConsent, s.State);
        Assert.Equal("neutral", s.Tone);
    }

    [Fact]
    public void A_client_in_backoff_says_until_when()
    {
        var s = ClientHealth.Of(Input(status: "Failed", next: Now.AddHours(1)));
        Assert.Equal(ClientHealth.StateBackingOff, s.State);
        Assert.StartsWith("Backing off until", s.Label);
    }

    [Fact]
    public void A_backoff_that_has_passed_is_just_failed()
    {
        var s = ClientHealth.Of(Input(status: "Failed", next: Now.AddMinutes(-1)));
        Assert.Equal(ClientHealth.StateFailed, s.State);
    }

    [Fact]
    public void Partly_connected_is_a_warning_not_an_error()
    {
        var s = ClientHealth.Of(Input(status: ClientHealth.Partial));
        Assert.Equal(ClientHealth.StatePartial, s.State);
        Assert.Equal("warning", s.Tone);
    }

    [Fact]
    public void A_clean_run_is_connected()
    {
        var s = ClientHealth.Of(Input(status: "Completed"));
        Assert.Equal(ClientHealth.StateConnected, s.State);
        Assert.Equal("good", s.Tone);
    }

    [Fact]
    public void A_client_never_collected_is_not_collected_yet()
    {
        var s = ClientHealth.Of(Input(status: null));
        Assert.Equal(ClientHealth.StateNotCollectedYet, s.State);
    }
}
