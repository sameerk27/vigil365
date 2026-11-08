using System.Globalization;
using System.Text.Json;

namespace M365SecurityDashboard.Api.Services.Common;

/// <summary>
/// Tolerant readers for Microsoft JSON. Microsoft adds, drops and re-types fields without notice, and
/// JsonElement.GetString/TryGetProperty throw on the wrong kind, so no reader here ever throws: a missing
/// or wrongly-typed value is null/false/empty, and callers decide whether that makes the record partial.
/// </summary>
public static class JsonRead
{
    /// <summary>Walks a property path. Null when any step is missing or not an object.</summary>
    public static JsonElement? Get(JsonElement e, params string[] path) => Walk(e, ignoreCase: false, path);

    /// <summary>As <see cref="Get"/>, matching names ignoring case (Power Platform admin APIs and
    /// Purview audit payloads are inconsistent about casing).</summary>
    public static JsonElement? GetIgnoreCase(JsonElement e, params string[] path) => Walk(e, ignoreCase: true, path);

    public static string? Str(JsonElement e, params string[] path) =>
        Get(e, path) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>String value, with empty/whitespace treated as absent.</summary>
    public static string? NonBlank(JsonElement e, params string[] path) =>
        Str(e, path) is { } s && !string.IsNullOrWhiteSpace(s) ? s : null;

    public static string? NonBlankIgnoreCase(JsonElement e, params string[] path) =>
        GetIgnoreCase(e, path) is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s && !string.IsNullOrWhiteSpace(s)
            ? s : null;

    /// <summary>Scalar as text: strings as-is, numbers and booleans as their JSON text.</summary>
    public static string? Text(JsonElement e, params string[] path) => Get(e, path) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    public static bool IsTrue(JsonElement e, params string[] path) => Get(e, path) is { ValueKind: JsonValueKind.True };

    public static int? Int(JsonElement e, params string[] path) =>
        Get(e, path) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;

    public static double? Number(JsonElement e, params string[] path) =>
        Get(e, path) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : null;

    /// <summary>ISO timestamp parsed with the invariant culture; a value without an offset is taken as UTC,
    /// matching how the Purview collector already reads it.</summary>
    public static DateTimeOffset? Date(JsonElement e, params string[] path) =>
        Str(e, path) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    /// <summary>String array; non-string and empty entries are dropped.</summary>
    public static IReadOnlyList<string> Strings(JsonElement e, params string[] path) =>
        Get(e, path) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!).Where(s => s.Length > 0).ToList()
            : [];

    private static JsonElement? Walk(JsonElement e, bool ignoreCase, string[] path)
    {
        var cur = e;
        foreach (var name in path)
        {
            // TryGetProperty throws on non-objects, so guard the kind before every step.
            if (cur.ValueKind != JsonValueKind.Object) return null;
            JsonElement? next = null;
            if (!ignoreCase)
            {
                if (cur.TryGetProperty(name, out var exact)) next = exact;
            }
            else
            {
                foreach (var p in cur.EnumerateObject())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { next = p.Value; break; }
            }
            if (next is not { } n) return null;
            cur = n;
        }
        return cur;
    }
}
