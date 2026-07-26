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
    {
        if (string.IsNullOrWhiteSpace(affectedEntitiesJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(affectedEntitiesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var result = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                // Entity JSON is camelCase by contract (locked by test after the
                // PascalCase bug that produced "System / N/A" rows).
                foreach (var key in new[] { "userPrincipalName", "deviceName", "targetName" })
                {
                    if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) result.Add(s!);
                    }
                }
            }
            return result;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Returns the first rule that suppresses this alert, or null. A rule applies
    /// when it is enabled, unexpired, scoped to this policy (or all policies), and
    /// — if it names an entity pattern — at least one affected entity matches.
    /// </summary>
    public static SuppressionRule? FindMatch(
        IEnumerable<SuppressionRule> rules,
        Guid policyId,
        string? affectedEntitiesJson,
        DateTimeOffset now)
    {
        var entities = ExtractEntities(affectedEntitiesJson);

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            if (rule.ExpiresAt is not null && rule.ExpiresAt <= now) continue;
            if (rule.PolicyId is not null && rule.PolicyId != policyId) continue;

            if (string.IsNullOrWhiteSpace(rule.EntityPattern))
            {
                // Policy-wide suppression. Requires an explicit policy scope —
                // a rule with neither policy nor entity would mute everything,
                // which is never what someone means.
                if (rule.PolicyId is null) continue;
                return rule;
            }

            if (entities.Any(e => EntityMatches(rule.EntityPattern, e))) return rule;
        }
        return null;
    }
}
