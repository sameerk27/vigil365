using System.Security.Claims;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// A resolved alert stays resolved. An analyst acting on a stale row (the
/// cross-client queue, or an Alert Center left open) must not move an alert
/// someone else resolved back to "acknowledged", nor overwrite who resolved it.
/// </summary>
public sealed class TriggeredAlertWorkflowTests
{
    private static readonly Guid Contoso = TestTenancy.TenantA;

    private static readonly ClaimsPrincipal Analyst = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Email, "ana@msp.test"), new Claim(ClaimTypes.Role, AppRoles.Analyst)], "test"));

    private static async Task<(EndpointHarness H, TriggeredAlert Alert)> HarnessAsync(string status, string? resolvedBy)
    {
        var h = new EndpointHarness(app => app.MapAlertsEndpoints(), services: s =>
        {
            s.AddScoped<AlertEvaluator>();
            s.AddScoped<PolicyBacktester>();
        });
        var resolvedAt = status is "resolved" or "auto_resolved" ? DateTimeOffset.UtcNow.AddMinutes(-5) : (DateTimeOffset?)null;
        var alert = new TriggeredAlert
        {
            Id = Guid.NewGuid(), PolicyId = Guid.NewGuid(), PolicyName = "Risky sign-ins spike", Severity = "high",
            Status = status, ResolvedAt = resolvedAt, ResolvedBy = resolvedBy,
        };
        await using var db = h.Db(Contoso);
        db.TriggeredAlerts.Add(alert);
        await db.SaveChangesAsync();
        return (h, alert);
    }

    private static async Task<TriggeredAlert> ReloadAsync(EndpointHarness h, Guid id)
    {
        await using var db = h.Db(Contoso);
        return await db.TriggeredAlerts.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    [Theory]
    [InlineData("resolved", "bob@msp.test", "resolved by bob@msp.test")]
    [InlineData("auto_resolved", "system", "resolved automatically")]
    public async Task Acknowledging_a_resolved_alert_is_refused_and_leaves_it_resolved(string status, string resolvedBy, string message)
    {
        var (h, alert) = await HarnessAsync(status, resolvedBy);
        await using var _h = h;

        var (code, body) = await h.SendAsync("POST", "/api/triggered-alerts/{id:guid}/acknowledge", Analyst, Contoso, routeValues: new { id = alert.Id });

        Assert.Equal(409, code);
        Assert.Contains(message, body);
        var row = await ReloadAsync(h, alert.Id);
        Assert.Equal(status, row.Status);
        Assert.Null(row.AcknowledgedAt);
        Assert.Equal(resolvedBy, row.ResolvedBy);
    }

    [Fact]
    public async Task Resolving_an_alert_again_keeps_who_resolved_it_and_when()
    {
        var (h, alert) = await HarnessAsync("resolved", "bob@msp.test");
        await using var _h = h;

        var (code, _) = await h.SendAsync("POST", "/api/triggered-alerts/{id:guid}/resolve", Analyst, Contoso, routeValues: new { id = alert.Id });

        Assert.Equal(409, code);
        var row = await ReloadAsync(h, alert.Id);
        Assert.Equal("bob@msp.test", row.ResolvedBy);
        Assert.Equal(alert.ResolvedAt, row.ResolvedAt);
    }

    [Theory]
    [InlineData("acknowledge", "acknowledged")]
    [InlineData("resolve", "resolved")]
    public async Task An_open_alert_can_still_be_acknowledged_or_resolved(string action, string expected)
    {
        var (h, alert) = await HarnessAsync("new", null);
        await using var _h = h;

        var (code, _) = await h.SendAsync("POST", $"/api/triggered-alerts/{{id:guid}}/{action}", Analyst, Contoso, routeValues: new { id = alert.Id });

        Assert.Equal(200, code);
        Assert.Equal(expected, (await ReloadAsync(h, alert.Id)).Status);
    }
}
