using System.Text.Json;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Decides whether a would-be alert is covered by a standing suppression rule.
/// Pure and static so the matching semantics — the part that can silently hide
/// real alerts if it is wrong — are unit-testable without a database.
/// </summary>
public static class SuppressionMatcher
{
    /// <summary>
    /// Matches an entity against a pattern supporting a single leading and/or
    /// trailing '*'. Case-insensitive. "*" alone matches anything non-empty.
    /// </summary>
    public static bool EntityMatches(string? pattern, string? entity)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;   // no restriction
        if (string.IsNullOrWhiteSpace(entity)) return false;   // pattern set, nothing to match

        var p = pattern.Trim();
        var e = entity.Trim();
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;

        var starts = p.StartsWith('*');
        var ends = p.EndsWith('*');
        var core = p.Trim('*');

        if (core.Length == 0) return true;                       // "*" or "**"
        if (starts && ends) return e.Contains(core, ci);
        if (starts) return e.EndsWith(core, ci);
        if (ends) return e.StartsWith(core, ci);
        return string.Equals(e, core, ci);
    }

    /// <summary>Entity identifiers from an alert's AffectedEntities JSON.
    /// Tolerates malformed JSON by returning nothing rather than throwing.</summary>
    public static IReadOnlyList<string> ExtractEntities(string? affectedEntitiesJson)
        => ExtractEntityRows(affectedEntitiesJson).SelectMany(ids => ids).ToList();

    /// <summary>The identifiers of each affected entity (one list per JSON array element).</summary>
    private static List<List<string>> ExtractEntityRows(string? affectedEntitiesJson)
    {
        if (string.IsNullOrWhiteSpace(affectedEntitiesJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(affectedEntitiesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var result = new List<List<string>>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var ids = new List<string>();
                // Entity JSON is camelCase by contract (locked by test after the
                // PascalCase bug that produced "System / N/A" rows).
                foreach (var key in new[] { "userPrincipalName", "deviceName", "targetName" })
                {
                    if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) ids.Add(s!);
                    }
                }
                result.Add(ids);
            }
            return result;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Returns a rule that suppresses this alert, or null. A rule applies when it
    /// is enabled, unexpired and scoped to this policy (or all policies). A
    /// policy-wide rule suppresses the alert outright. Entity rules suppress it
    /// only when every affected entity matches one of them: an alert lists all
    /// of a policy's entities, so muting it because one matched would hide every
    /// other user or device in it too. An entity with no identifier matches none.
    /// </summary>
    public static SuppressionRule? FindMatch(
        IEnumerable<SuppressionRule> rules,
        Guid policyId,
        string? affectedEntitiesJson,
        DateTimeOffset now)
    {
        var applicable = rules
            .Where(r => r.Enabled && (r.ExpiresAt is null || r.ExpiresAt > now) && (r.PolicyId is null || r.PolicyId == policyId))
            .ToList();

        // Policy-wide suppression. Requires an explicit policy scope — a rule
        // with neither policy nor entity would mute everything, which is never
        // what someone means.
        var policyWide = applicable.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.EntityPattern) && r.PolicyId is not null);
        if (policyWide is not null) return policyWide;

        var entityRules = applicable.Where(r => !string.IsNullOrWhiteSpace(r.EntityPattern)).ToList();
        var entities = ExtractEntityRows(affectedEntitiesJson);
        if (entityRules.Count == 0 || entities.Count == 0) return null;

        SuppressionRule? first = null;
        foreach (var ids in entities)
        {
            var rule = entityRules.FirstOrDefault(r => ids.Any(id => EntityMatches(r.EntityPattern, id)));
            if (rule is null) return null; // this entity is not suppressed: raise the alert
            first ??= rule;
        }
        return first;
    }
}
