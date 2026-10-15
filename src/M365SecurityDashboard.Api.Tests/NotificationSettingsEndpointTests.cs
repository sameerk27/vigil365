using System.Security.Claims;
using System.Text.Json;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>Saving the install-wide notification settings and a client's routing.</summary>
public sealed class NotificationSettingsEndpointTests
{
    private static readonly Guid Contoso = TestTenancy.TenantA;

    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Email, "admin@msp.test"), new Claim(ClaimTypes.Role, AppRoles.Admin)], "test"));

    [Theory]
    [InlineData("weekly", "weekly")]
    [InlineData("Weekly", "weekly")]
    [InlineData("daily", "daily")]
    [InlineData("hourly", "daily")] // anything else is the default
    public async Task The_digest_frequency_is_saved_and_returned(string sent, string stored)
    {
        await using var h = new EndpointHarness(app => app.MapNotificationsEndpoints());

        var (put, _) = await h.SendAsync("PUT", "/api/notification-settings", Admin(), body: new { emailEnabled = true, emailDigest = true, digestFrequency = sent, digestHourUtc = 8 });
        var (get, body) = await h.SendAsync("GET", "/api/notification-settings", Admin());

        Assert.Equal(200, put);
        Assert.Equal(200, get);
        Assert.Equal(stored, JsonDocument.Parse(body).RootElement.GetProperty("digestFrequency").GetString());
        await using var db = h.Db();
        Assert.Equal(stored, (await db.NotificationSettings.AsNoTracking().SingleAsync()).DigestFrequency);
    }

    [Fact]
    public async Task Client_only_routing_needs_a_client_destination()
    {
        await using var h = new EndpointHarness(app => app.MapNotificationsEndpoints());

        var (nowhere, _) = await h.SendAsync("PUT", "/api/notification-routing", Admin(), Contoso,
            body: new { notifyMsp = false, notifyClient = true });
        var (teamsOnly, _) = await h.SendAsync("PUT", "/api/notification-routing", Admin(), Contoso,
            body: new { notifyMsp = false, notifyClient = true, teamsWebhookUrl = "https://contoso.webhook.office.com/webhookb2/x" });

        Assert.Equal(400, nowhere);
        Assert.Equal(200, teamsOnly);
    }
}
