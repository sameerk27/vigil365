using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>POST /api/api-tokens refuses a token that would be born expired (cli-15/cli-17).</summary>
public sealed class ApiTokenEndpointTests
{
    [Fact]
    public async Task A_past_expiry_is_refused_and_nothing_is_issued()
    {
        await using var h = new EndpointHarness(app => app.MapIntegrationsEndpoints(), services: s => s.AddScoped<ApiTokenService>());

        var (status, body) = await h.SendAsync("POST", "/api/api-tokens",
            body: new ApiTokenCreateRequest("SIEM", "alerts:read", DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal(400, status);
        Assert.Contains("future", body);
        await using var db = h.Db();
        Assert.False(await db.ApiTokens.AnyAsync());

        (status, _) = await h.SendAsync("POST", "/api/api-tokens",
            body: new ApiTokenCreateRequest("SIEM", "alerts:read", DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Equal(200, status);
    }
}
