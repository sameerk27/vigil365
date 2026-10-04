using System.Text.Json;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// MSP_V12_PLAN.md M2: one Graph permission list, and a readiness check of the
/// MSP app registration that replaces the old in-app registration endpoint.
/// </summary>
public sealed class MspAppStatusTests
{
    // ── One permission list (D5) ──────────────────────────────────────────────

    [Fact]
    public void Embedded_permission_list_is_the_expected_shape()
    {
        var names = GraphPermissionList.Required.Select(p => p.Name).ToList();
        Assert.Equal(14, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // The three the old register-app.ps1 list was missing — clients would have lost these features.
        Assert.Contains("SecurityAlert.Read.All", names);
        Assert.Contains("SharePointTenantSettings.Read.All", names);
        Assert.Contains("PrivilegedAccess.Read.AzureAD", names);
        // Read-only product: no write permission may be required.
        Assert.DoesNotContain(names, n => n.Contains("Write", StringComparison.OrdinalIgnoreCase));
        // Never something clients would be asked to consent to just for the MSP status card.
        Assert.DoesNotContain("Application.Read.All", names);
        Assert.Single(GraphPermissionList.Optional);
    }

    [Fact]
    public void Permissions_doc_lists_exactly_the_embedded_permissions()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "graph-permissions.md"));
        foreach (var p in GraphPermissionList.Required.Concat(GraphPermissionList.Optional))
            Assert.Contains($"`{p.Name}`", doc);
    }

    [Fact]
    public void Register_script_reads_the_shared_list_and_has_no_list_of_its_own()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "register-app.ps1"));
        Assert.Contains("graph-permissions.json", script);
        Assert.DoesNotContain("\"SecurityEvents.Read.All\"", script); // the stale hard-coded list
        Assert.DoesNotContain("\"IdentityRiskyUser.Read.All\"", script);
    }

    // ── Readiness evaluation ──────────────────────────────────────────────────

    private const string Expected = "https://vigil.msp.test/consented";
    private static readonly string[] Required = ["SecurityAlert.Read.All", "AuditLog.Read.All"];

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static readonly JsonElement GraphSp = Json("""
        { "appRoles": [ { "value": "SecurityAlert.Read.All", "id": "r-alert" }, { "value": "AuditLog.Read.All", "id": "r-audit" } ] }
        """);

    private static JsonElement App(string audience, string[] redirects, string[] roleIds) => Json(JsonSerializer.Serialize(new
    {
        signInAudience = audience,
        web = new { redirectUris = redirects },
        requiredResourceAccess = new[] { new { resourceAppId = GraphPermissionList.GraphAppId, resourceAccess = roleIds.Select(id => new { id, type = "Role" }) } },
    }));

    [Fact]
    public void Ready_when_multi_tenant_redirect_registered_and_all_permissions_requested()
    {
        var r = MspAppStatus.Evaluate(App("AzureADMultipleOrgs", ["https://vigil.msp.test/consented/"], ["r-alert", "r-audit"]), GraphSp, Required, Expected);
        Assert.True(r.Ready);
        Assert.Empty(r.MissingPermissions!);
    }

    [Fact]
    public void Single_tenant_app_is_not_ready_for_client_consent()
    {
        var r = MspAppStatus.Evaluate(App("AzureADMyOrg", [Expected], ["r-alert", "r-audit"]), GraphSp, Required, Expected);
        Assert.False(r.MultiTenant);
        Assert.False(r.Ready);
    }

    [Fact]
    public void Missing_consent_redirect_is_reported()
    {
        var r = MspAppStatus.Evaluate(App("AzureADMultipleOrgs", ["https://other.test/consented"], ["r-alert", "r-audit"]), GraphSp, Required, Expected);
        Assert.False(r.ConsentRedirectRegistered);
        Assert.False(r.Ready);
    }

    [Fact]
    public void Missing_permissions_are_named()
    {
        var r = MspAppStatus.Evaluate(App("AzureADMultipleOrgs", [Expected], ["r-alert"]), GraphSp, Required, Expected);
        Assert.Equal(["AuditLog.Read.All"], r.MissingPermissions);
        Assert.False(r.Ready);
    }

    [Fact]
    public void Unreadable_is_never_ready()
    {
        var r = MspAppStatus.Unreadable(Expected, "no permission");
        Assert.False(r.Ready);
        Assert.Null(r.MultiTenant);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "graph-permissions.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (graph-permissions.json) not found");
    }
}
