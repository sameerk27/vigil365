using System.Text.Json;
using M365SecurityDashboard.Api.Services;
using static M365SecurityDashboard.Api.Services.Common.JsonRead;

namespace M365SecurityDashboard.Api.Endpoints;

/// <summary>
/// The fields every alerts_v2-backed dashboard section shows. Serialises to the camelCase names the
/// SPA reads. Severity and status are never null: the SPA lower-cases them, and Graph can send null.
/// </summary>
internal sealed record AlertV2Row(
    string? Id, string? Title, string Severity, string Status, string? Category, string? ServiceSource,
    string? CreatedDateTime, string? Description, string? AlertWebUrl, IReadOnlyList<string> MitreTechniques)
{
    public static AlertV2Row From(JsonElement a) => new(
        Str(a, "id"), Str(a, "title"),
        Str(a, "severity") ?? "unknown", Str(a, "status") ?? "unknown",
        Str(a, "category"), Str(a, "serviceSource"), Str(a, "createdDateTime"),
        Str(a, "description"), Str(a, "alertWebUrl"), Strings(a, "mitreTechniques"));

    /// <summary>Newest first; <paramref name="filter"/> is an OData $filter expression.</summary>
    public static async Task<List<AlertV2Row>> QueryAsync(GraphApiClient graph, string filter, CancellationToken ct, int top = 50)
    {
        var items = await graph.GetCollectionAsync(
            $"/v1.0/security/alerts_v2?$top={top}&$filter={filter}&$orderby=createdDateTime desc", ct);
        return items.Select(From).ToList();
    }

    /// <summary>
    /// The user an email alert was aimed at, or null. Graph marks each evidence item's kind with
    /// @odata.type. The mailbox and the message recipient come first, because a userEvidence item
    /// on an email alert can be someone else, such as the person who reported the message.
    /// </summary>
    public static string? TargetedUser(JsonElement alert)
    {
        if (Get(alert, "evidence") is not { ValueKind: JsonValueKind.Array } evidence) return null;
        var items = evidence.EnumerateArray().ToList();
        string? First(string type, Func<JsonElement, string?> read) => items
            .Where(e => (Str(e, "@odata.type") ?? "").EndsWith(type, StringComparison.OrdinalIgnoreCase))
            .Select(read).FirstOrDefault(v => v is not null);

        return First("mailboxEvidence", e => NonBlank(e, "upn") ?? NonBlank(e, "userAccount", "userPrincipalName") ?? NonBlank(e, "primaryAddress"))
            ?? First("analyzedMessageEvidence", e => NonBlank(e, "recipientEmailAddress"))
            ?? First("userEvidence", e => NonBlank(e, "userAccount", "userPrincipalName"));
    }

    public const int InsiderRiskScanLimit = 200;

    /// <summary>
    /// Graph's serviceSource has no Insider Risk value and detectionSource can't be used in $filter, so
    /// the newest alerts are read and picked here. detectionSource only says Insider Risk when the
    /// request sends Prefer: include-unknown-enum-members.
    /// </summary>
    public static (List<AlertV2Row> Alerts, string? Note) PickInsiderRisk(IReadOnlyList<JsonElement> newestFirst)
    {
        var alerts = newestFirst.Where(a => Str(a, "detectionSource") == "microsoftInsiderRiskManagement").Select(From).ToList();
        var note = newestFirst.Count >= InsiderRiskScanLimit
            ? $"Vigil365 checked the newest {InsiderRiskScanLimit} Defender alerts only, so older Insider Risk alerts are not shown."
            : null;
        return (alerts, note);
    }

    public static Dictionary<string, int> CountBy(IEnumerable<AlertV2Row> rows, Func<AlertV2Row, string?> key) =>
        rows.GroupBy(r => key(r) ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
}
