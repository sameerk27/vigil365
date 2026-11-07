using System.Text.Json;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The PIM panel listed role assignments without expanding the principal, so every row had no name
/// (the client shows "Unknown"). The principal is expanded now and the role name comes from the role
/// definitions (one $expand per request).
/// </summary>
public class PimAssignmentsTests
{
    private static List<JsonElement> Items(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();

    [Fact]
    public void The_principal_and_the_role_are_named()
    {
        var rows = PimAssignments.Project(
            Items("""[{"id":"ra1","roleDefinitionId":"def-ga","principal":{"displayName":"Ana","userPrincipalName":"ana@contoso.com"}}]"""),
            Items("""[{"id":"def-ga","displayName":"Global Administrator"}]"""));
        var r = Assert.Single(rows);
        Assert.Equal("Ana", r.PrincipalDisplayName);
        Assert.Equal("ana@contoso.com", r.PrincipalUpn);
        Assert.Equal("Global Administrator", r.RoleName);
    }

    [Fact]
    public void An_expanded_role_definition_still_wins_and_unknowns_stay_null()
    {
        var rows = PimAssignments.Project(
            Items("""[{"id":"ra1","roleDefinitionId":"x","roleDefinition":{"displayName":"Exchange Administrator"},"principal":{"displayName":"Ops app"}},{"id":"ra2","roleDefinitionId":"missing"}]"""),
            Items("""[{"id":"x","displayName":"Other"}]"""));
        Assert.Equal("Exchange Administrator", rows[0].RoleName);
        Assert.Null(rows[0].PrincipalUpn);
        Assert.Null(rows[1].RoleName);
        Assert.Null(rows[1].PrincipalDisplayName);
    }

    [Fact]
    public void The_query_expands_the_principal()
    {
        Assert.Contains("$expand=principal", PimAssignments.AssignmentsPath);
        Assert.Contains("roleDefinitions", PimAssignments.RoleDefinitionsPath);
    }
}
