using System.Text.Json;

namespace M365SecurityDashboard.Api.Services;

/// <summary>One row of the PIM panel.</summary>
public sealed record PimAssignmentRow(string? Id, string? PrincipalDisplayName, string? PrincipalUpn, string? RoleName);

/// <summary>
/// Role assignments with the principal expanded; the role names come from the role definitions, as a
/// request takes one $expand. Without the principal every row had no name.
/// </summary>
public static class PimAssignments
{
    public const string AssignmentsPath = "/v1.0/roleManagement/directory/roleAssignments?$top=100&$expand=principal";
    public const string RoleDefinitionsPath = "/v1.0/roleManagement/directory/roleDefinitions?$select=id,displayName";

    public static List<PimAssignmentRow> Project(IEnumerable<JsonElement> assignments, IEnumerable<JsonElement> roleDefinitions)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in roleDefinitions)
            if (Str(d, "id") is string id && Str(d, "displayName") is string dn) names[id] = dn;
        return assignments.Select(a =>
        {
            var p = a.TryGetProperty("principal", out var pe) && pe.ValueKind == JsonValueKind.Object ? pe : (JsonElement?)null;
            var rd = a.TryGetProperty("roleDefinition", out var re) && re.ValueKind == JsonValueKind.Object ? re : (JsonElement?)null;
            var role = (rd is JsonElement r ? Str(r, "displayName") : null)
                ?? (Str(a, "roleDefinitionId") is string rid && names.TryGetValue(rid, out var n) ? n : null);
            return new PimAssignmentRow(Str(a, "id"), p is JsonElement x ? Str(x, "displayName") : null, p is JsonElement y ? Str(y, "userPrincipalName") : null, role);
        }).ToList();
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
