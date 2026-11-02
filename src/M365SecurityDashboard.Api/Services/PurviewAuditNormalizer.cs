using System.Text.Json;

namespace M365SecurityDashboard.Api.Services;

/// <summary>One Purview audit record, reduced to what activity policies match on.</summary>
public sealed record NormalizedAuditEvent(string Product, string Activity, string? ActorUpn, string? TargetName, string? Result);

/// <summary>
/// Turns Microsoft Purview audit records for Copilot Studio agents, Power Automate,
/// Power Apps and Power Platform admin into activity names an operator reads, with
/// the risky detail folded in ("Flow created (high-risk connector: HTTP)"). Activity
/// policies match only on that name, so the existing policy engine, suppression
/// rules and affected-entity lists work without knowing any of these products.
///
/// Copilot Studio's event labels are documented by Microsoft and matched exactly.
/// Power Automate and Power Apps document friendly names only, not the operation
/// strings, so those are classified by the record's fields and by keywords in the
/// operation. Tune against a live tenant (docs/COPILOT_POWER_PLATFORM_PLAN.md §6);
/// records that match nothing are skipped and their operation names logged.
/// Pure: no I/O, so every mapping is unit-tested.
/// </summary>
public static class PurviewAuditNormalizer
{
    public const string Source = "purviewAudit";

    public const string CopilotAgents = "Copilot agents";
    public const string PowerAutomate = "Power Automate";
    public const string PowerApps = "Power Apps";
    public const string PowerPlatformAdmin = "Power Platform admin";
    public static readonly IReadOnlyList<string> Products = [CopilotAgents, PowerAutomate, PowerApps, PowerPlatformAdmin];

    /// <summary>
    /// Copilot Studio event labels (the audit record's operation carries the label).
    /// A label that contains another comes first. A null activity is a record that is
    /// skipped: usage, or a follow-up of an event already counted.
    /// </summary>
    internal static readonly (string Label, string? Activity)[] CopilotStudioLabels =
    [
        ("BotUpdateOperation-BotAuthUpdate", "Agent authentication changed"),
        ("BotUpdateOperation-BotShare",      "Agent shared"),
        ("BotUpdateOperation-BotPublish",    "Agent published"),
        ("BotUpdateOperation-BotNameUpdate", "Agent renamed"),
        ("BotUpdateOperation-BotIconUpdate", "Agent icon changed"),
        ("BotAppInsightsUpdate",             "Agent logging settings changed"),
        ("BotDeleteCleanup",                 null),
        ("BotComponentCollectionCreate",     "Agent component collection created"),
        ("BotComponentCollectionUpdate",     "Agent component collection changed"),
        ("BotComponentCollectionDelete",     "Agent component collection deleted"),
        ("BotComponentCreate",               "Agent component created"),
        ("BotComponentUpdate",               "Agent component changed"),
        ("BotComponentDelete",               "Agent component deleted"),
        ("AIPluginOperationCreate",          "Agent action added"),
        ("AIPluginOperationUpdate",          "Agent action changed"),
        ("AIPluginOperationDelete",          "Agent action removed"),
        ("EnvironmentVariableCreate",        "Agent environment variable created"),
        ("EnvironmentVariableUpdate",        "Agent environment variable changed"),
        ("EnvironmentVariableDelete",        "Agent environment variable deleted"),
        ("BotCreate",                        "Agent created"),
        ("BotDelete",                        "Agent deleted"),
        ("CopilotInteraction",               null),
    ];

    /// <summary>The operations the Copilot Studio audit search asks for. Usage (CopilotInteraction) is left out.</summary>
    public static IReadOnlyList<string> CopilotStudioOperations { get; } =
        CopilotStudioLabels.Where(l => l.Activity is not null).Select(l => l.Label).ToList();

    /// <summary>The audit record types the Power Platform search asks for.</summary>
    public static IReadOnlyList<string> PowerPlatformRecordTypes { get; } =
        ["microsoftFlow", "powerAppsApp", "powerAppsResource", "powerPlatformAdminDlp", "powerPlatformAdminEnvironment", "powerPlatformServiceActivity"];

    /// <summary>
    /// The record as an operator should see it, or null when it is not one of ours or
    /// is not worth storing (usage, trials, launches).
    /// </summary>
    /// <param name="highRiskConnectors">Connector names or "Prefix*" patterns that make a flow high-risk.</param>
    /// <param name="isExternal">Whether a user principal name is outside the organisation.</param>
    public static NormalizedAuditEvent? Normalize(JsonElement record, IReadOnlyCollection<string> highRiskConnectors, Func<string, bool> isExternal)
    {
        if (record.ValueKind != JsonValueKind.Object) return null;
        var operation = Str(record, "operation") ?? "";
        var recordType = Str(record, "auditLogRecordType") ?? "";
        var data = record.TryGetProperty("auditData", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        var actor = Str(record, "userPrincipalName") ?? Field(data, "UserUPN");
        var result = ResultOf(Field(data, "ResultStatus"));

        foreach (var (label, activity) in CopilotStudioLabels)
        {
            if (!operation.Contains(label, StringComparison.OrdinalIgnoreCase)) continue;
            if (activity is null) return null;
            var agent = Field(data, "BotName") ?? Field(data, "BotSchemaName") ?? Field(data, "BotId") ?? Str(record, "objectId");
            return new(CopilotAgents, activity, actor, agent, result);
        }

        if (Is(recordType, "powerPlatformAdminDlp"))
            return new(PowerPlatformAdmin, $"Power Platform DLP policy changed ({Readable(operation)})", actor, Field(data, "PolicyName") ?? Str(record, "objectId"), result);
        if (Is(recordType, "powerPlatformAdminEnvironment"))
            return new(PowerPlatformAdmin, EnvironmentActivity(operation), actor, Field(data, "EnvironmentName") ?? Str(record, "objectId"), result);

        if (Is(recordType, "microsoftFlow") || Field(data, "FlowConnectorNames") is not null || Field(data, "FlowDetailsUrl") is not null)
            return Flow(record, data, operation, actor, result, highRiskConnectors, isExternal);

        var props = PropertyCollection(data);
        if (props.TryGetValue("powerplatform.analytics.resource.type", out var type) && type.Equals("PowerApp", StringComparison.OrdinalIgnoreCase)
            || recordType.StartsWith("powerApps", StringComparison.OrdinalIgnoreCase))
            return App(record, props, operation, actor, result);

        return null;
    }

    private static string EnvironmentActivity(string operation)
    {
        var op = Squash(operation);
        if (op.Contains("delete")) return "Environment deleted";
        if (op.Contains("create") || op.Contains("provision")) return "Environment created";
        return $"Environment changed ({Readable(operation)})";
    }

    private static NormalizedAuditEvent? Flow(JsonElement record, JsonElement data, string operation, string? actor, string? result,
        IReadOnlyCollection<string> highRiskConnectors, Func<string, bool> isExternal)
    {
        var op = Squash(operation);
        if (op.Contains("trial")) return null; // licence trials, not a change to anything

        var flow = Field(data, "FlowDisplayName") ?? Field(data, "FlowName") ?? FlowIdFromUrl(Field(data, "FlowDetailsUrl")) ?? Str(record, "objectId");
        var recipient = Field(data, "RecipientUPN");
        var risky = Connectors(Field(data, "FlowConnectorNames")).Where(c => IsHighRisk(c, highRiskConnectors)).ToList();
        var qualifier = risky.Count > 0 ? $" (high-risk connector: {string.Join(", ", risky)})" : "";

        string activity;
        if (recipient is not null || op.Contains("permission"))
        {
            var who = recipient ?? "unknown user";
            if (op.Contains("delete") || op.Contains("remove")) activity = $"Flow sharing removed: {who}";
            else if (recipient is not null && isExternal(recipient)) activity = $"Flow shared (external: {recipient})";
            else if (IsOwner(Field(data, "SharingPermission"))) activity = $"Flow co-owner added: {who}";
            else activity = $"Flow shared (run-only): {who}";
        }
        else if (op.Contains("delete"))
            activity = Field(data, "UserTypeInitiated") == "2" ? "Flow deleted by admin" : "Flow deleted";
        else if (op.Contains("create"))
            activity = "Flow created" + qualifier;
        else if (op.Contains("edit") || op.Contains("update"))
            activity = "Flow edited" + qualifier;
        else
            activity = $"Flow activity ({Readable(operation)})" + qualifier;

        return new(PowerAutomate, activity, actor, flow, result);
    }

    // Ordered: the first keyword found in the squashed activity name wins. A null
    // activity skips the record (usage and cosmetic settings).
    private static readonly (string Keyword, string? Activity)[] AppKeywords =
    [
        ("launch", null),
        ("datalossprevention", null),
        ("featured", null),
        ("hero", null),
        ("bypass", "App consent bypass changed"),
        ("thirdparty", "Third-party apps allowed"),
        ("quarantine", "App quarantine state changed"),
        ("conditionalaccess", "App conditional access changed"),
        ("owner", "App owner changed"),
    ];

    private static NormalizedAuditEvent? App(JsonElement record, Dictionary<string, string> props, string operation, string? actor, string? result)
    {
        var name = props.GetValueOrDefault("powerplatform.analytics.activity.name") ?? operation;
        var op = Squash(name);
        var app = props.GetValueOrDefault("powerplatform.analytics.resource.power_app.display_name") ?? Str(record, "objectId");
        actor ??= props.GetValueOrDefault("enduser.principal_name");

        string? activity = null;
        var matched = false;
        foreach (var (keyword, mapped) in AppKeywords)
        {
            if (!op.Contains(keyword)) continue;
            activity = mapped;
            matched = true;
            break;
        }
        if (!matched)
        {
            activity =
                op.Contains("permission") ? (op.Contains("delete") || op.Contains("remove") ? "App permission removed" : "App permissions changed")
                : op.Contains("version") ? (op.Contains("delete") ? "App version deleted" : "App version restored")
                : op.Contains("publish") ? "App published"
                : op.Contains("restore") ? "App restored"
                : op.Contains("delete") ? "App deleted"
                : op.Contains("import") ? "App imported"
                : op.Contains("create") ? "App created"
                : op.Contains("consent") ? "App API consent granted"
                : op.Contains("edit") || op.Contains("patch") || op.Contains("update") ? "App edited"
                : $"App activity ({Readable(name)})";
        }
        return activity is null ? null : new(PowerApps, activity, actor, app, result);
    }

    /// <summary>Connector names from the flow's record: a comma-, semicolon- or pipe-separated string, or a JSON array.</summary>
    internal static IReadOnlyList<string> Connectors(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var text = raw.Trim();
        if (text.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()
                        : Field(e, "displayName") ?? Field(e, "name"))
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (JsonException) { /* not JSON after all: split it */ }
        }
        return text.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static bool IsHighRisk(string connector, IReadOnlyCollection<string> patterns)
    {
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var pattern = p.Trim();
            if (pattern.EndsWith('*')
                ? connector.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : connector.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Guests carry #EXT# in their UPN; otherwise a domain the tenant has not verified is outside the organisation.</summary>
    public static bool IsExternal(string upn, IReadOnlySet<string> verifiedDomains)
    {
        if (upn.Contains("#EXT#", StringComparison.OrdinalIgnoreCase)) return true;
        var at = upn.LastIndexOf('@');
        if (at < 0 || verifiedDomains.Count == 0) return false;
        return !verifiedDomains.Contains(upn[(at + 1)..]);
    }

    private static bool IsOwner(string? sharingPermission) =>
        sharingPermission is not null
        && (sharingPermission.Trim() == "3"
            || sharingPermission.Contains("owner", StringComparison.OrdinalIgnoreCase)
            || sharingPermission.Contains("readwrite", StringComparison.OrdinalIgnoreCase));

    private static string? FlowIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p.Equals("flows", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : null;
    }

    private static string? ResultOf(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "succeeded" or "success" or "true" => "success",
        "failed" or "failure" or "false" => "failure",
        _ => null,
    };

    private static Dictionary<string, string> PropertyCollection(JsonElement data)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in data.EnumerateObject())
        {
            if (!p.Name.Equals("PropertyCollection", StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in p.Value.EnumerateArray())
            {
                var name = Field(item, "Name");
                var value = Field(item, "Value");
                if (name is not null && value is not null) map[name] = value;
            }
        }
        return map;
    }

    private static bool Is(string recordType, string expected) => recordType.Equals(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower case without spaces, hyphens or underscores, so "Edited app permission" and "EditAppPermission" match the same keywords.</summary>
    private static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Readable(string operation) => string.IsNullOrWhiteSpace(operation) ? "unknown operation" : operation.Trim();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString() : null;

    /// <summary>A top-level property of an object, matched ignoring case, as text.</summary>
    private static string? Field(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
        {
            if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var text = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                JsonValueKind.Array => p.Value.GetRawText(),
                _ => null,
            };
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return null;
    }
}
