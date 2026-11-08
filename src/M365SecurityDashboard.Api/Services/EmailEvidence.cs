using System.Text.Json;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// The mailbox an email alert targets, from its evidence: the alerts_v2 shape Graph sends
/// (<c>@odata.type</c>: mailboxEvidence <c>upn</c>/<c>primaryAddress</c>, userEvidence
/// <c>userAccount.userPrincipalName</c>, analyzedMessageEvidence <c>recipientEmailAddress</c>) and the
/// older <c>entityType</c> shape (User / Mailbox).
/// </summary>
public static class EmailEvidence
{
    public static string? TargetedUpn(JsonElement alert)
    {
        if (!alert.TryGetProperty("evidence", out var ev) || ev.ValueKind != JsonValueKind.Array) return null;
        foreach (var e in ev.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var entityType = Str(e, "entityType");
            var odata = Str(e, "@odata.type") ?? "";
            var upn = entityType == "User" || odata.EndsWith("userEvidence", StringComparison.OrdinalIgnoreCase) ? Str(Obj(e, "userAccount"), "userPrincipalName")
                : entityType == "Mailbox" ? Str(Obj(e, "mailbox"), "userPrincipalName")
                : odata.EndsWith("mailboxEvidence", StringComparison.OrdinalIgnoreCase) ? First(Str(e, "upn"), Str(e, "primaryAddress"), Str(Obj(e, "userAccount"), "userPrincipalName"))
                : odata.EndsWith("analyzedMessageEvidence", StringComparison.OrdinalIgnoreCase) ? Str(e, "recipientEmailAddress")
                : null;
            if (!string.IsNullOrEmpty(upn)) return upn;
        }
        return null;
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static JsonElement? Obj(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement? e, string prop) =>
        e is JsonElement o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
