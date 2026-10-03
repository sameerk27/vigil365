using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>Where a client's alerts go: the pure routing rule and the MSP digest schedule.</summary>
public sealed class NotificationRoutingTests
{
    private static NotificationSettings Install() => new()
    {
        Id = 1, EmailEnabled = true, SmtpHost = "smtp", DefaultRecipient = "soc@msp.test",
        TeamsEnabled = true, TeamsWebhookUrl = "dp:msp-teams", WebhookEnabled = true, WebhookUrl = "dp:msp-hook", WebhookSigningSecret = "dp:sig",
        MinSeverity = "low", LastDigestAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void No_routing_row_means_everything_goes_to_the_msp_exactly_as_before()
    {
        var cfg = NotificationRouting.Apply(Install(), null);
        Assert.Equal("soc@msp.test", cfg.DefaultRecipient);
        Assert.Equal("dp:msp-teams", cfg.TeamsWebhookUrl);
        Assert.Equal("dp:msp-hook", cfg.WebhookUrl);
        Assert.Equal("low", cfg.MinSeverity);
    }

    [Fact]
    public void Apply_returns_a_copy_and_never_mutates_the_install_row()
    {
        var install = Install();
        var cfg = NotificationRouting.Apply(install, new TenantNotificationRouting { NotifyMsp = false, NotifyClient = true, RecipientEmail = "it@client.test" });
        Assert.NotSame(install, cfg);
        Assert.Equal("soc@msp.test", install.DefaultRecipient);
        Assert.Equal("it@client.test", cfg.DefaultRecipient);
    }

    [Fact]
    public void Both_means_msp_and_client_recipients_and_the_client_channels_win_when_set()
    {
        var cfg = NotificationRouting.Apply(Install(), new TenantNotificationRouting
        {
            NotifyMsp = true, NotifyClient = true, RecipientEmail = "it@client.test", TeamsWebhookUrl = "dp:client-teams", MinSeverity = "high",
        });
        Assert.Equal("soc@msp.test,it@client.test", cfg.DefaultRecipient);
        Assert.Equal("dp:client-teams", cfg.TeamsWebhookUrl); // client's own Teams
        Assert.Equal("dp:msp-hook", cfg.WebhookUrl);         // no client webhook → MSP's
        Assert.Equal("high", cfg.MinSeverity);
    }

    [Fact]
    public void Client_only_drops_every_msp_channel_and_the_msp_signing_secret()
    {
        var cfg = NotificationRouting.Apply(Install(), new TenantNotificationRouting { NotifyMsp = false, NotifyClient = true, RecipientEmail = "it@client.test" });
        Assert.Equal("it@client.test", cfg.DefaultRecipient);
        Assert.Null(cfg.TeamsWebhookUrl);
        Assert.Null(cfg.WebhookUrl);
        Assert.Null(cfg.WebhookSigningSecret);
    }

    [Fact]
    public void Client_only_with_no_recipient_yields_no_email_recipient_rather_than_the_msp()
    {
        var cfg = NotificationRouting.Apply(Install(), new TenantNotificationRouting { NotifyMsp = false, NotifyClient = true });
        Assert.Null(cfg.DefaultRecipient);
    }

    [Fact]
    public void Only_a_route_that_includes_the_msp_may_fall_back_to_its_from_mailbox()
    {
        Assert.True(NotificationRouting.Apply(Install(), null).FromAddressFallback);
        Assert.True(NotificationRouting.Apply(Install(), new TenantNotificationRouting { NotifyMsp = true }).FromAddressFallback);
        Assert.False(NotificationRouting.Apply(Install(), new TenantNotificationRouting { NotifyMsp = false, NotifyClient = true }).FromAddressFallback);
    }

    [Fact]
    public void A_client_webhook_never_carries_the_msp_signing_secret()
    {
        // Payloads the client receives signed with the MSP's key could be
        // replayed into the MSP's own webhook receiver.
        var cfg = NotificationRouting.Apply(Install(), new TenantNotificationRouting { NotifyMsp = true, NotifyClient = true, WebhookUrl = "dp:client-hook" });
        Assert.Equal("dp:client-hook", cfg.WebhookUrl);
        Assert.Null(cfg.WebhookSigningSecret);
        Assert.Equal("dp:sig", NotificationRouting.Apply(Install(), new TenantNotificationRouting { NotifyMsp = true }).WebhookSigningSecret);
    }

    [Fact]
    public void A_policy_address_stands_in_for_the_msp_default_recipient_only()
    {
        Assert.Equal("owner@msp.test", NotificationRouting.Apply(Install(), null, "owner@msp.test").DefaultRecipient);
        Assert.Equal("owner@msp.test,it@client.test", NotificationRouting.Apply(Install(),
            new TenantNotificationRouting { NotifyMsp = true, NotifyClient = true, RecipientEmail = "it@client.test" }, "owner@msp.test").DefaultRecipient);
        Assert.Equal("it@client.test", NotificationRouting.Apply(Install(),
            new TenantNotificationRouting { NotifyMsp = false, NotifyClient = true, RecipientEmail = "it@client.test" }, "owner@msp.test").DefaultRecipient);
    }

    [Fact]
    public void Per_tenant_digest_timestamps_come_from_the_routing_row_not_the_install_row()
    {
        var routing = new TenantNotificationRouting { LastDigestAt = null };
        var cfg = NotificationRouting.Apply(Install(), routing);
        Assert.Null(cfg.LastDigestAt); // install row's 2026-01-01 must not leak across tenants
    }

    [Theory]
    [InlineData(false, 7, null, "2026-09-03T07:10:00Z", false)]        // disabled
    [InlineData(true, 7, null, "2026-09-03T07:10:00Z", true)]          // due, never sent
    [InlineData(true, 7, null, "2026-09-03T08:10:00Z", false)]         // wrong hour
    [InlineData(true, 7, "2026-09-03T07:05:00Z", "2026-09-03T07:50:00Z", false)] // already today
    [InlineData(true, 7, "2026-09-02T07:05:00Z", "2026-09-03T07:50:00Z", true)]  // yesterday
    public void Msp_digest_is_due_once_a_day_at_the_configured_hour(bool enabled, int hour, string? last, string now, bool expected)
    {
        var install = new NotificationSettings { MspDigestEnabled = enabled, MspDigestHourUtc = hour, LastMspDigestAt = last is null ? null : DateTimeOffset.Parse(last) };
        Assert.Equal(expected, TenantRollupService.IsMspDigestDue(install, DateTimeOffset.Parse(now)));
    }

    [Fact]
    public void Msp_digest_html_lists_every_client_and_encodes_names()
    {
        var rows = new List<TenantRollupService.Row>
        {
            new(Guid.NewGuid(), "Bad <Client>", true, DateTimeOffset.UtcNow, "Failed", "consent expired", new(2, 1, 0, 0), 1, 0, "error"),
            new(Guid.NewGuid(), "Good Co", true, DateTimeOffset.UtcNow, "Completed", null, new(0, 0, 1, 3), 0, 0, "good"),
        };
        var html = TenantRollupService.MspDigestHtml(rows, DateTimeOffset.UtcNow, "https://vigil.msp.test/");
        Assert.Contains("Bad &lt;Client&gt;", html);
        Assert.DoesNotContain("Bad <Client>", html);
        Assert.Contains("Good Co", html);
        Assert.Contains("consent expired", html);
        Assert.Contains("https://vigil.msp.test/#/clients", html);
        Assert.Contains("2 clients", TenantRollupService.MspDigestSubject(rows));
        Assert.Contains("1 not collecting", TenantRollupService.MspDigestSubject(rows));
    }

    [Fact]
    public void Health_ranks_not_collecting_and_critical_as_error_high_as_warning()
    {
        var t = new ClientTenant { Name = "x", LastCollectionAt = DateTimeOffset.UtcNow, LastCollectionStatus = "Completed" };
        Assert.Equal("neutral", TenantRollupService.HealthOf(false, t, new(0, 0, 0, 0)));
        Assert.Equal("good", TenantRollupService.HealthOf(true, t, new(0, 0, 0, 0)));
        Assert.Equal("warning", TenantRollupService.HealthOf(true, t, new(0, 1, 0, 0)));
        Assert.Equal("error", TenantRollupService.HealthOf(true, t, new(1, 0, 0, 0)));
        Assert.Equal("error", TenantRollupService.HealthOf(true, new ClientTenant { Name = "y", LastCollectionStatus = "Failed" }, new(0, 0, 0, 0)));
    }
}
