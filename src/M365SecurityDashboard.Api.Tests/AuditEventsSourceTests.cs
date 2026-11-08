using System.Text.Json;
using M365SecurityDashboard.Api.Endpoints;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The Power Platform page lists every Purview audit product at once (source=purviewAudit);
/// Tenant Activity still filters by one product or by the Entra directory audit.
/// </summary>
public sealed class AuditEventsSourceTests
{
    private static async Task<EndpointHarness> Seeded()
    {
        var h = new EndpointHarness(app => app.MapPlatformEndpoints(), services: s => s.AddScoped<EntityProfileBuilder>());
        await using var db = h.Db(TestTenancy.TenantA);
        var now = DateTimeOffset.UtcNow;
        AuditEvent E(string id, string source, string? category, string activity) => new()
        {
            ExternalId = id, Source = source, Category = category, Activity = activity, OccurredAt = now.AddHours(-1), CollectedAt = now,
        };
        db.AuditEvents.AddRange(
            E("a", "directoryAudit", "RoleManagement", "Add member to role"),
            E("b", PurviewAuditNormalizer.Source, PurviewAuditNormalizer.PowerAutomate, "Flow created"),
            E("c", PurviewAuditNormalizer.Source, PurviewAuditNormalizer.CopilotAgents, "Agent shared"),
            E("d", PurviewAuditNormalizer.Source, PurviewAuditNormalizer.PowerPlatformAdmin, "Power Platform DLP policy deleted"));
        await db.SaveChangesAsync();
        return h;
    }

    private static async Task<string[]> Activities(EndpointHarness h, string source)
    {
        var (status, body) = await h.SendAsync("GET", "/api/audit-events", tenant: TestTenancy.TenantA, query: $"?source={Uri.EscapeDataString(source)}");
        Assert.Equal(200, status);
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("activity").GetString()!).Order().ToArray();
    }

    [Fact]
    public async Task Purview_audit_as_a_source_returns_every_Power_Platform_product_and_no_Entra_events()
    {
        await using var h = await Seeded();
        Assert.Equal(["Agent shared", "Flow created", "Power Platform DLP policy deleted"], await Activities(h, "purviewAudit"));
    }

    [Fact]
    public async Task One_product_and_Entra_still_filter_as_before()
    {
        await using var h = await Seeded();
        Assert.Equal(["Flow created"], await Activities(h, "Power Automate"));
        Assert.Equal(["Add member to role"], await Activities(h, "entra"));
    }
}
