using System.Text.Json;
using static M365SecurityDashboard.Api.Services.Common.JsonRead;

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
    /// Power Platform admin operations Microsoft logs under the record type
    /// PowerPlatformAdministratorActivity: DLP policies in their current format, environment
    /// groups and their rules (sharing limits for apps and flows), an admin taking System
    /// Administrator, and Customer Lockbox. Graph's auditLogRecordType has no member for that
    /// type, so the record-type search can't ask for it; a third search asks by these
    /// documented operation names instead.
    /// </summary>
    public static IReadOnlyList<string> PowerPlatformAdminOperations { get; } =
    [
        "GovernanceApiPolicyOperation",
        "NewEnvironmentGroup", "UpdateEnvironmentGroup", "DeleteEnvironmentGroup",
        "EnvironmentAddedToEnvironmentGroup", "EnvironmentRemovedFromEnvironmentGroup",
        "CreateRuleSetOperation", "UpdateRuleSetOperation", "DeleteRuleSetOperation",
        "CreateRuleBasedPolicyOperation", "UpdateRuleBasedPolicyOperation", "DeleteRuleBasedPolicyOperation",
        "CreateRuleBasedPolicyAssignmentOperation", "DeleteRuleBasedPolicyAssignmentOperation",
        "ApplyAdminRole", "LockboxRequestOperation",
    ];

    /// <summary>
    /// The record as an operator should see it, or null when it is not one of ours or
    /// is not worth storing (usage, trials, launches).
    /// </summary>
    /// <param name="highRiskConnectors">Connector names or "Prefix*" patterns that make a flow high-risk.</param>
    /// <param name="isExternal">Whether a user principal name is outside the organisation.</param>
    public static NormalizedAuditEvent? Normalize(JsonElement record, IReadOnlyCollection<string> highRiskConnectors, Func<string, bool> isExternal)
    {
        if (record.ValueKind != JsonValueKind.Object) return null;
        var operation = NonBlank(record, "operation") ?? "";
        var recordType = NonBlank(record, "auditLogRecordType") ?? "";
        var data = record.TryGetProperty("auditData", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        var actor = NonBlank(record, "userPrincipalName") ?? Field(data, "UserUPN");
        var result = ResultOf(Field(data, "ResultStatus"));

        foreach (var (label, activity) in CopilotStudioLabels)
        {
            if (!operation.Contains(label, StringComparison.OrdinalIgnoreCase)) continue;
            if (activity is null) return null;
            var agent = Field(data, "BotName") ?? Field(data, "BotSchemaName") ?? Field(data, "BotId") ?? NonBlank(record, "objectId");
            return new(CopilotAgents, activity, actor, agent, result);
        }

        if (PowerPlatformAdminOperations.FirstOrDefault(o => o.Equals(operation, StringComparison.OrdinalIgnoreCase)) is { } adminOperation)
            return Admin(record, data, adminOperation, actor, result);

        if (Is(recordType, "powerPlatformAdminDlp"))
            return new(PowerPlatformAdmin, result == "failure" ? AdminFailed(operation) : DlpActivity(operation), actor, Field(data, "PolicyName") ?? NonBlank(record, "objectId"), result);
        if (Is(recordType, "powerPlatformAdminEnvironment"))
            return new(PowerPlatformAdmin, EnvironmentActivity(operation, PropertyCollection(data)), actor, Field(data, "EnvironmentName") ?? NonBlank(record, "objectId"), result);

        if (Is(recordType, "microsoftFlow") || Field(data, "FlowConnectorNames") is not null || Field(data, "FlowDetailsUrl") is not null)
            return Flow(record, data, operation, actor, result, highRiskConnectors, isExternal);

        var props = PropertyCollection(data);
        if (props.TryGetValue("powerplatform.analytics.resource.type", out var type) && type.Equals("PowerApp", StringComparison.OrdinalIgnoreCase)
            || recordType.StartsWith("powerApps", StringComparison.OrdinalIgnoreCase))
            return App(record, props, operation, actor, result);

        return null;
    }

    // Microsoft documents friendly names only for environment events ("Changed property on
    // environment", "Copied", "Restored"), so they are told apart by keyword. Order matters:
    // "recover deleted" is a recovery, not a delete. The security group shows up in the
    // properties of a "changed property" record, not in its operation (assumed; check live).
    private static string EnvironmentActivity(string operation, Dictionary<string, string> props)
    {
        var op = Squash(operation);
        if (op.Contains("copy") || op.Contains("copied")) return "Environment copied";
        if (op.Contains("recover")) return "Environment recovered";
        if (op.Contains("restore")) return "Environment restored";
        if (op.Contains("move")) return "Environment moved";
        if (op.Contains("convert")) return "Environment type converted";
        if (op.Contains("reset")) return "Environment reset";
        if (op.Contains("backup") || op.Contains("backedup")) return "Environment backed up";
        if (op.Contains("delete")) return "Environment deleted";
        if (op.Contains("create") || op.Contains("provision")) return "Environment created";
        // After the lifecycle checks and only on a property change: a record for a new or
        // deleted environment can list its security group without having changed it.
        if (op.Contains("securitygroup")
            || (op.Contains("property") && props.Any(p => Squash(p.Key).Contains("securitygroup") || Squash(p.Value).Contains("securitygroup"))))
            return "Environment security group changed";
        return $"Environment changed ({Readable(operation)})";
    }

    // Named so no "deleted" or "changed" rule fires for something that didn't happen.
    private static string AdminFailed(string operation) => $"Power Platform admin action failed ({Readable(operation)})";

    /// <summary>A DLP policy change named by what it did; deleting one is what weakens protection most.</summary>
    private static string DlpActivity(string operation)
    {
        var op = Squash(operation);
        if (op.Contains("connector") || op.Contains("pattern") || op.Contains("exempt"))
            return $"Power Platform connector policy changed ({Readable(operation)})";
        if (op.Contains("delete") || op.Contains("remove")) return "Power Platform DLP policy deleted";
        if (op.Contains("create") || op.Contains("new")) return "Power Platform DLP policy created";
        return $"Power Platform DLP policy changed ({Readable(operation)})";
    }

    /// <summary>
    /// Records Microsoft logs as PowerPlatformAdministratorActivity. Their detail sits in
    /// PropertyCollection: the DLP sub-operation (for example "DeleteDlpPolicy") and the
    /// resource's display name. Keys are from Microsoft's examples; anything missing falls back
    /// to the operation name, so a record is never dropped for an unexpected shape.
    /// </summary>
    private static NormalizedAuditEvent Admin(JsonElement record, JsonElement data, string operation, string? actor, string? result)
    {
        var props = PropertyCollection(data);
        actor ??= props.GetValueOrDefault("enduser.principal_name");
        var target = props.GetValueOrDefault("powerplatform.analytics.resource.display_name")
            ?? props.GetValueOrDefault("powerplatform.analytics.resource.environment.name")
            ?? Field(data, "PolicyName") ?? Field(data, "EnvironmentName")
            ?? props.GetValueOrDefault("powerplatform.analytics.resource.id")
            ?? NonBlank(record, "objectId");
        var op = Squash(operation);
        var detail = operation;

        string activity;
        if (op == "governanceapipolicyoperation")
        {
            var sub = props.GetValueOrDefault("powerplatform.analytics.resource.tenant.governance.api_policy.operation_name")
                ?? props.GetValueOrDefault("powerplatform.analytics.activity.name")
                ?? Field(data, "Operation") ?? operation;
            detail = sub;
            activity = DlpActivity(sub);
        }
        else if (op.Contains("ruleset") || op.Contains("rulebasedpolicy"))
            activity = $"Environment group rules changed ({operation})";
        else if (op.Contains("environmentgroup"))
            activity = $"Environment group changed ({operation})";
        else if (op == "applyadminrole")
        {
            activity = "Admin took System Administrator in an environment";
            target = props.GetValueOrDefault("powerplatform.analytics.resource.environment.name")
                ?? props.GetValueOrDefault("powerplatform.analytics.resource.environment.id") ?? target;
        }
        else
        {
            // Lockbox: the request's state key isn't documented, so take whichever property says it.
            var state = props.FirstOrDefault(p => p.Key.EndsWith(".status", StringComparison.OrdinalIgnoreCase)
                || p.Key.EndsWith(".state", StringComparison.OrdinalIgnoreCase)).Value;
            activity = $"Customer Lockbox request ({(string.IsNullOrWhiteSpace(state) ? "updated" : state.Trim())})";
        }
        result ??= ResultOf(props.GetValueOrDefault("powerplatform.analytics.resource.tenant.governance.api_policy.operation_result"))
            ?? ResultOf(props.GetValueOrDefault("powerplatform.analytics.operation.is_successful"));
        return new(PowerPlatformAdmin, result == "failure" ? AdminFailed(detail) : activity, actor, target, result);
    }

    private static NormalizedAuditEvent? Flow(JsonElement record, JsonElement data, string operation, string? actor, string? result,
        IReadOnlyCollection<string> highRiskConnectors, Func<string, bool> isExternal)
    {
        var op = Squash(operation);
        if (op.Contains("trial")) return null; // licence trials, not a change to anything

        var flow = Field(data, "FlowDisplayName") ?? Field(data, "FlowName") ?? FlowIdFromUrl(Field(data, "FlowDetailsUrl")) ?? NonBlank(record, "objectId");
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
        var app = props.GetValueOrDefault("powerplatform.analytics.resource.power_app.display_name") ?? NonBlank(record, "objectId");
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
    /// <summary>A top-level property of an object, matched ignoring case, as text.</summary>
    private static string? Field(JsonElement e, string name)
    {
        var text = GetIgnoreCase(e, name) switch
        {
            { ValueKind: JsonValueKind.String } v => v.GetString(),
            { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array } v => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
