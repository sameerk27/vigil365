using System.Text.RegularExpressions;
using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Ties the Graph calls the app really makes to the permission list it asks admins to
/// consent to. The single-list refactor once dropped <c>SecurityEvents.Read.All</c>
/// while <c>/security/secureScores</c> still needed it: nothing failed in CI, and the
/// Secure Score card said "Needs permission" on the first live install. Adding a Graph
/// call now fails this test until its permission is named here and is in the list.
/// </summary>
public sealed class GraphPermissionCoverageTests
{
    // First path segments of each Graph call -> the application permission(s) Microsoft
    // documents for it (any one is enough). Application.Read.All is intentionally outside
    // the shared list: only the MSP's own readiness check uses it.
    private static readonly Dictionary<string, string[]> Needs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["security/alerts_v2"] = ["SecurityAlert.Read.All"],
        ["security/incidents"] = ["SecurityIncident.Read.All"],
        ["security/secureScores"] = ["SecurityEvents.Read.All"],
        ["security/identities/healthIssues"] = ["SecurityIdentitiesHealth.Read.All"],
        ["security/attackSimulation/simulations"] = ["AttackSimulation.ReadWrite.All", "AttackSimulation.Read.All"],
        ["identityProtection/riskyUsers"] = ["IdentityRiskyUser.Read.All"],
        ["identityProtection/riskDetections"] = ["IdentityRiskEvent.Read.All"],
        ["auditLogs/signIns"] = ["AuditLog.Read.All"],
        ["auditLogs/directoryAudits"] = ["AuditLog.Read.All"],
        ["reports/authenticationMethods/userRegistrationDetails"] = ["AuditLog.Read.All", "Reports.Read.All"],
        ["deviceManagement/managedDevices"] = ["DeviceManagementManagedDevices.Read.All"],
        ["identity/conditionalAccess/policies"] = ["Policy.Read.All"],
        ["admin/sharepoint/settings"] = ["SharePointTenantSettings.Read.All"],
        ["admin/serviceAnnouncement/issues"] = ["ServiceHealth.Read.All"],
        ["roleManagement/directory/roleAssignments"] = ["Directory.Read.All", "RoleManagement.Read.Directory"],
        ["users"] = ["Directory.Read.All"],
        ["organization"] = ["Directory.Read.All"],
        ["subscribedSkus"] = ["Directory.Read.All"],
        ["directoryRoles"] = ["Directory.Read.All"],
        ["servicePrincipals"] = ["Application.Read.All"],
        ["applications"] = ["Application.Read.All"],
    };

    private static string ApiDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var api = Path.Combine(d.FullName, "src", "M365SecurityDashboard.Api");
            if (Directory.Exists(api)) return api;
        }
        throw new DirectoryNotFoundException("src/M365SecurityDashboard.Api not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> GraphPathsInCode()
    {
        var rx = new Regex("\"/(?:v1\\.0|beta)/([A-Za-z][A-Za-z0-9_]*(?:/[A-Za-z][A-Za-z0-9_]*)*)", RegexOptions.Compiled);
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(ApiDir(), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{sep}Migrations{sep}") && !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}")))
            foreach (Match m in rx.Matches(File.ReadAllText(file)))
                yield return m.Groups[1].Value;
    }

    [Fact]
    public void Every_graph_call_in_the_code_is_classified()
    {
        var unknown = GraphPathsInCode().Distinct().Where(p => !Needs.ContainsKey(p)).OrderBy(p => p).ToList();
        Assert.True(unknown.Count == 0,
            "These Graph calls have no permission mapped in GraphPermissionCoverageTests.Needs - add each with the " +
            "permission Microsoft documents for it, and add that permission to graph-permissions.json: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Every_permission_the_code_needs_is_in_the_shared_list_so_admin_consent_grants_it()
    {
        var inList = GraphPermissionList.Required.Concat(GraphPermissionList.Optional)
            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = GraphPathsInCode().Distinct().Where(Needs.ContainsKey)
            .Where(p => !Needs[p].Contains("Application.Read.All") && !Needs[p].Any(inList.Contains))
            .Select(p => $"{p} (needs {string.Join(" or ", Needs[p])})").OrderBy(s => s).ToList();
        Assert.True(missing.Count == 0, "Graph calls whose permission is not in graph-permissions.json: " + string.Join("; ", missing));
    }

    [Fact]
    public void Secure_score_has_its_permission()
        => Assert.Contains(GraphPermissionList.Required, p => p.Name == "SecurityEvents.Read.All");
}
