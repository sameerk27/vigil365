using System.Security.Claims;
using System.Text.Json;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// GET /api/notification-routing is readable by Analysts (routing is shown to them
/// read-only), so it must not hand them a client's channel secrets.
/// </summary>
public sealed class NotificationRoutingEndpointTests
{
    private const string TeamsUrl = "https://contoso.webhook.office.com/webhookb2/secret-path";
    private static readonly Guid Contoso = TestTenancy.TenantA;

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Email, $"{role.ToLowerInvariant()}@msp.test"), new Claim(ClaimTypes.Role, role)], "test"));

    private static async Task<JsonElement> GetAsRoleAsync(string role)
    {
        await using var h = new EndpointHarness(app => app.MapNotificationsEndpoints());
        await using (var db = h.Db(Contoso))
        {
            db.TenantNotificationRoutings.Add(new TenantNotificationRouting
            {
                NotifyClient = true,
                TeamsWebhookUrl = h.Get<SecretProtector>().Protect(TeamsUrl),
                WebhookUrl = h.Get<SecretProtector>().Protect("https://siem.contoso.test/hook"),
            });
            await db.SaveChangesAsync();
        }

        var (status, body) = await h.SendAsync("GET", "/api/notification-routing", User(role), Contoso);
        Assert.Equal(200, status);
        Assert.DoesNotContain("siem.contoso.test", body); // the generic webhook is never returned
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Analysts_see_only_that_a_client_teams_webhook_is_set()
    {
        var routing = await GetAsRoleAsync(AppRoles.Analyst);
        Assert.Equal(JsonValueKind.Null, routing.GetProperty("teamsWebhookUrl").ValueKind);
        Assert.True(routing.GetProperty("hasTeamsWebhookUrl").GetBoolean());
        Assert.True(routing.GetProperty("hasWebhookUrl").GetBoolean());
    }

    [Fact]
    public async Task Admins_who_edit_routing_get_the_teams_webhook_back()
    {
        var routing = await GetAsRoleAsync(AppRoles.Admin);
        Assert.Equal(TeamsUrl, routing.GetProperty("teamsWebhookUrl").GetString());
        Assert.True(routing.GetProperty("hasTeamsWebhookUrl").GetBoolean());
    }
}
