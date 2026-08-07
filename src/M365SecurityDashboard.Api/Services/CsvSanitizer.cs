namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// One place for CSV field encoding, shared by every export path.
///
/// Two separate concerns, both required:
///  • RFC-4180 quoting so commas/quotes/newlines survive a round-trip.
///  • Formula-injection neutralisation. Alert titles, user display names and
///    audit actor names are tenant-controlled. A field beginning =, +, - or @
///    is evaluated as a formula when the export is opened in Excel or Sheets
///    (e.g. =HYPERLINK(...) exfiltrating data on open). Prefixing an apostrophe
///    forces text and is invisible to the reader.
/// </summary>
public static class CsvSanitizer
{
    private static readonly char[] Dangerous = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>Neutralises a leading formula trigger. Does not quote.</summary>
    public static string Neutralize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        return Array.IndexOf(Dangerous, s[0]) >= 0 ? "'" + s : s;
    }

    /// <summary>Full CSV field encoding: formula-safe, then RFC-4180 quoted.</summary>
    public static string Field(string? s)
    {
        var v = Neutralize(s);
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}
