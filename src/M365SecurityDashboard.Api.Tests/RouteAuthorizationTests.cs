using System.Reflection;
using M365SecurityDashboard.Api.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Who may call each route, read from the endpoints the modules really map. Other endpoint
/// tests run handlers directly, past authorization, so without this a route could lose its
/// policy and every test would still pass. A new route, or a changed policy, has to be
/// added to the list below on purpose.
/// </summary>
public sealed class RouteAuthorizationTests
{
    /// <summary>Says yes to every type, so no handler parameter is taken for a request body. No request is ever sent.</summary>
    private sealed class EverythingIsAService(IServiceProvider inner) : IServiceProvider, IServiceProviderIsService
    {
        public bool IsService(Type serviceType) => true;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? this : inner.GetService(serviceType);
    }

    private sealed class Factory : IServiceProviderFactory<IServiceCollection>
    {
        public IServiceCollection CreateBuilder(IServiceCollection services) => services;
        public IServiceProvider CreateServiceProvider(IServiceCollection services) => new EverythingIsAService(services.BuildServiceProvider());
    }

    /// <summary>Every route of every Map…Endpoints module, as "METHOD pattern = access".</summary>
    private static List<string> ActualRoutes()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Host.UseServiceProviderFactory(new Factory());
        var app = builder.Build();

        // Found by reflection, so a new module is covered without touching this test.
        var modules = typeof(AdminEndpoints).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(AdminEndpoints).Namespace && t.IsAbstract && t.IsSealed)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.Name.StartsWith("Map") && m.Name.EndsWith("Endpoints"));
        foreach (var module in modules) module.Invoke(null, [app]);

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
                .Select(method => $"{method} {e.RoutePattern.RawText} = {Access(e)}"))
            .OrderBy(r => r.Split(' ')[1], StringComparer.Ordinal).ThenBy(r => r, StringComparer.Ordinal)
            .ToList();
    }

    private static string Access(RouteEndpoint e)
    {
        if (e.Metadata.GetMetadata<IAllowAnonymous>() is not null) return "Anonymous";
        var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).OfType<string>().Distinct().ToList();
        return policies.Count == 0 ? "SignedIn" : string.Join("+", policies);
    }

    /// <summary>
    /// Of the anonymous routes, the SIEM ones take an API token in the handler, and /consented
    /// is Microsoft's redirect back, checked against the one-time nonce in the consent link.
    /// "SignedIn" means any role, Viewer included, so it is for reads only: every write needs
    /// Analyst or Admin.
    /// </summary>
    private const string Expected = """
GET /api/admin/audit-log = RequireAdmin
GET /api/admin/audit-log/export = RequireAdmin
GET /api/admin/audit-log/verify = RequireAdmin
GET /api/admin/users = RequireAdmin
POST /api/admin/users = RequireAdmin
DELETE /api/admin/users/{email} = RequireAdmin
POST /api/admin/users/{email}/invite = RequireAdmin
PUT /api/admin/users/{email}/role = RequireAdmin
GET /api/alert-coverage = SignedIn
POST /api/alert-coverage/enable/{id} = RequireAnalyst
GET /api/alert-notes/{kind}/{targetId} = SignedIn
POST /api/alert-notes/{kind}/{targetId} = RequireAnalyst
GET /api/alert-policies = SignedIn
POST /api/alert-policies = RequireAnalyst
POST /api/alert-policies/backtest = RequireAnalyst
POST /api/alert-policies/evaluate = RequireAnalyst
GET /api/alert-policies/export = RequireAnalyst
POST /api/alert-policies/import = RequireAnalyst
GET /api/alert-policies/tenant-overrides = SignedIn
DELETE /api/alert-policies/{id:guid} = RequireAnalyst
PUT /api/alert-policies/{id:guid} = RequireAnalyst
DELETE /api/alert-policies/{id:guid}/tenant-override = RequireAdmin
PUT /api/alert-policies/{id:guid}/tenant-override = RequireAdmin
GET /api/alerts = SignedIn
POST /api/alerts/{id:long}/workbench = RequireAnalyst
GET /api/api-tokens = RequireAdmin
POST /api/api-tokens = RequireAdmin
POST /api/api-tokens/{id:guid}/revoke = RequireAdmin
GET /api/audit-events = SignedIn
GET /api/auth/config = Anonymous
GET /api/auth/me = SignedIn
GET /api/baseline = RequireAnalyst
POST /api/baseline/capture = RequireAdmin
POST /api/collector/run = RequireAnalyst
GET /api/collector/runs = SignedIn
GET /api/dashboard/attack-simulation = SignedIn
GET /api/dashboard/ca-gaps = RequireAnalyst
GET /api/dashboard/conditional-access = SignedIn
GET /api/dashboard/copilot = SignedIn
GET /api/dashboard/defender-alerts = SignedIn
GET /api/dashboard/devices = SignedIn
GET /api/dashboard/dlp-alerts = SignedIn
GET /api/dashboard/email-protection = SignedIn
GET /api/dashboard/identity = SignedIn
GET /api/dashboard/identity-health = SignedIn
GET /api/dashboard/inactive-users = SignedIn
GET /api/dashboard/insider-risk = SignedIn
GET /api/dashboard/licenses = SignedIn
GET /api/dashboard/mcas-alerts = SignedIn
GET /api/dashboard/mde-vulnerabilities = SignedIn
GET /api/dashboard/mdi-alerts = SignedIn
GET /api/dashboard/overview = SignedIn
GET /api/dashboard/password-expiry = SignedIn
GET /api/dashboard/pim = SignedIn
GET /api/dashboard/privileged-roles = SignedIn
GET /api/dashboard/purview = SignedIn
GET /api/dashboard/risk-detections = SignedIn
GET /api/dashboard/securescore = SignedIn
GET /api/dashboard/security-incidents = SignedIn
GET /api/dashboard/servicehealth = SignedIn
GET /api/dashboard/sharing-posture = RequireAnalyst
GET /api/dashboard/signin-locations = SignedIn
GET /api/dashboard/trends = SignedIn
GET /api/entity/{kind}/{id} = RequireAnalyst
GET /api/metrics = RequireAnalyst
GET /api/notification-health = RequireAnalyst
GET /api/notification-log = RequireAnalyst
GET /api/notification-routing = RequireAnalyst
PUT /api/notification-routing = RequireAdmin
GET /api/notification-settings = RequireAdmin
PUT /api/notification-settings = RequireAdmin
POST /api/notification-settings/test = RequireAdmin
GET /api/posture = SignedIn
GET /api/posture/report.html = SignedIn
GET /api/purview-audit/status = SignedIn
GET /api/recommendations = SignedIn
GET /api/report-schedules = RequireAnalyst
POST /api/report-schedules = RequireAdmin
DELETE /api/report-schedules/{id:guid} = RequireAdmin
PUT /api/report-schedules/{id:guid} = RequireAdmin
POST /api/report-schedules/{id:guid}/run-now = RequireAdmin
GET /api/reports/exec-digest/preview = RequireAnalyst
GET /api/setup/graph = RequireAdmin
POST /api/setup/graph = RequireAdmin
GET /api/setup/msp-app-status = RequireAdmin
GET /api/setup/permissions = RequireAnalyst
GET /api/setup/status = RequireAnalyst
GET /api/siem/alerts = Anonymous
GET /api/siem/health = Anonymous
GET /api/suppression-rules = RequireAnalyst
POST /api/suppression-rules = RequireAdmin
DELETE /api/suppression-rules/{id:guid} = RequireAdmin
PUT /api/suppression-rules/{id:guid} = RequireAdmin
GET /api/tenants/ = RequireAdmin
POST /api/tenants/ = RequireAdmin
GET /api/tenants/alerts = SignedIn
GET /api/tenants/assignments = RequireAdmin
PUT /api/tenants/assignments/{email} = RequireAdmin
GET /api/tenants/me = SignedIn
GET /api/tenants/rollup = SignedIn
DELETE /api/tenants/{id:guid} = RequireAdmin
GET /api/tenants/{id:guid} = RequireAdmin
PUT /api/tenants/{id:guid} = RequireAdmin
GET /api/tenants/{id:guid}/consent-url = RequireAdmin
DELETE /api/tenants/{id:guid}/credentials = RequireAdmin
PUT /api/tenants/{id:guid}/credentials = RequireAdmin
POST /api/tenants/{id:guid}/test = RequireAdmin
GET /api/triggered-alerts = SignedIn
GET /api/triggered-alerts/metrics = RequireAnalyst
POST /api/triggered-alerts/{id:guid}/acknowledge = RequireAnalyst
POST /api/triggered-alerts/{id:guid}/assign = RequireAnalyst
POST /api/triggered-alerts/{id:guid}/reopen = RequireAnalyst
POST /api/triggered-alerts/{id:guid}/resolve = RequireAnalyst
POST /api/triggered-alerts/{id:guid}/snooze = RequireAnalyst
POST /api/triggered-alerts/{id:guid}/unsnooze = RequireAnalyst
GET /consented = Anonymous
GET /health = Anonymous
""";

    [Fact]
    public void Every_route_has_the_access_it_is_meant_to_have()
    {
        var expected = Expected.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var actual = ActualRoutes();

        var unexpected = actual.Except(expected).ToList();
        var missing = expected.Except(actual).ToList();
        Assert.True(unexpected.Count == 0 && missing.Count == 0,
            "Routes differ from the list in RouteAuthorizationTests. If the change is meant, update the list." +
            "\nIn the code, not the list:\n  " + string.Join("\n  ", unexpected) +
            "\nIn the list, not the code:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Only_the_listed_routes_are_open_to_anyone_or_write_with_no_role()
    {
        var routes = ActualRoutes();

        Assert.Equal(
            ["GET /api/auth/config", "GET /api/siem/alerts", "GET /api/siem/health", "GET /consented", "GET /health"],
            routes.Where(r => r.EndsWith(" = Anonymous")).Select(r => r[..^" = Anonymous".Length]).Order(StringComparer.Ordinal));
        // Every write needs a role: Viewers only read.
        Assert.Empty(routes.Where(r => r.EndsWith(" = SignedIn") && !r.StartsWith("GET ")));
    }
}
