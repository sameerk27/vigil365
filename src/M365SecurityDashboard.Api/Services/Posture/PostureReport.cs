using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace M365SecurityDashboard.Api.Services.Posture;

/// <summary>
/// The client-presentable licence posture report: one self-contained HTML file an MSP
/// can hand to a client. Every value from the tenant is HTML-encoded; the brand colour
/// is accepted only as #RRGGBB. Baseline references are identifiers only.
/// </summary>
public static partial class PostureReport
{
    public const string DefaultAccent = "#2563eb";

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();

    public static string Html(PostureResult r, string clientName, string preparedBy, string? accent, DateTimeOffset now)
    {
        var colour = accent is not null && HexColour().IsMatch(accent) ? accent : DefaultAccent;
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var date = now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        var s = r.Summary;

        var actions = r.Findings
            .Where(f => f.Status is PostureStatus.Fail or PostureStatus.LicensedNotConfigured)
            .OrderBy(f => f.Status == PostureStatus.Fail ? 0 : 1)
            .Take(5).ToList();
        var unused = r.Findings.Where(f => f.Status == PostureStatus.LicensedNotConfigured).ToList();
        var upgrades = r.Findings.Where(f => f.Status == PostureStatus.NotLicensed).ToList();
        var unknown = r.Findings.Where(f => f.Status == PostureStatus.Unknown).ToList();

        var b = new StringBuilder();
        b.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        b.Append("<title>Microsoft 365 security and licence review: ").Append(E(clientName)).Append("</title><style>");
        b.Append("body{margin:0;background:#f1f5f9;font-family:'Segoe UI',system-ui,sans-serif;color:#0f172a}");
        b.Append("main{max-width:860px;margin:24px auto;background:#fff;padding:40px 48px;border-radius:6px}");
        b.Append("h1{font-size:26px;margin:6px 0 4px}h2{font-size:16px;margin:28px 0 10px;color:").Append(colour).Append('}');
        b.Append("table{width:100%;border-collapse:collapse;font-size:13px}th{text-align:left;font-size:11px;text-transform:uppercase;letter-spacing:.05em;color:#576579;padding:8px 10px;border-bottom:1px solid #e2e8f0}td{padding:9px 10px;border-bottom:1px solid #f1f5f9;vertical-align:top}");
        b.Append(".tiles{display:grid;grid-template-columns:repeat(5,1fr);gap:8px}.tile{border:1px solid #e2e8f0;border-radius:8px;padding:10px 12px}.tile b{display:block;font-size:22px}");
        b.Append(".muted{color:#576579;font-size:12.5px}footer{margin-top:32px;padding-top:14px;border-top:1px solid #e2e8f0;font-size:11.5px;color:#576579}");
        b.Append("@media print{body{background:#fff}main{margin:0;padding:0}}</style></head><body><main>");

        b.Append("<header style=\"border-bottom:3px solid ").Append(colour).Append(";padding-bottom:16px\">");
        b.Append("<div class=\"muted\" style=\"text-transform:uppercase;letter-spacing:.08em;font-weight:700\">Microsoft 365 security and licence review</div>");
        b.Append("<h1>").Append(E(clientName)).Append("</h1>");
        b.Append("<div class=\"muted\">").Append(E(date)).Append(" · prepared by ").Append(E(preparedBy)).Append("</div></header>");

        b.Append("<h2>Summary</h2><p>").Append(s.Pass).Append(" of ").Append(s.Total).Append(" checks pass. ")
            .Append(s.LicensedNotConfigured).Append(" licensed features aren't set up, ")
            .Append(s.Fail).Append(" settings are below the recommended minimum, and ")
            .Append(s.NotLicensed).Append(" controls would need another licence.</p>");
        b.Append("<div class=\"tiles\">");
        foreach (var (label, n) in new[] { ("Pass", s.Pass), ("Fail", s.Fail), ("Paid for, not used", s.LicensedNotConfigured), ("Needs another licence", s.NotLicensed), ("Unknown", s.Unknown) })
            b.Append("<div class=\"tile\"><span class=\"muted\">").Append(E(label)).Append("</span><b>").Append(n).Append("</b></div>");
        b.Append("</div>");

        if (actions.Count > 0)
        {
            b.Append("<h2>Top actions</h2><ol>");
            foreach (var f in actions)
                b.Append("<li><strong>").Append(E(f.Check.Title)).Append(".</strong> ").Append(E(f.Detail)).Append("</li>");
            b.Append("</ol>");
        }

        if (unused.Count > 0)
        {
            b.Append("<h2>Paid for, not used</h2><table><thead><tr><th>Check</th><th>Included in</th><th>Found</th></tr></thead><tbody>");
            foreach (var f in unused)
                b.Append("<tr><td>").Append(E(f.Check.Title)).Append("</td><td>").Append(E(f.LicensedBy)).Append("</td><td>").Append(E(f.Detail)).Append("</td></tr>");
            b.Append("</tbody></table>");
        }

        if (upgrades.Count > 0)
        {
            b.Append("<h2>Options that need another licence</h2><table><thead><tr><th>Control</th><th>Available with</th></tr></thead><tbody>");
            foreach (var f in upgrades)
                b.Append("<tr><td>").Append(E(f.Check.Title)).Append("</td><td>").Append(E(string.Join(" or ", f.UnlockedBy))).Append("</td></tr>");
            b.Append("</tbody></table>");
        }

        b.Append("<h2>Baselines</h2><table><thead><tr><th>Baseline</th><th>Passing</th></tr></thead><tbody>");
        b.Append("<tr><td>CISA SCuBA</td><td>").Append(r.Scuba.Pass).Append(" of ").Append(r.Scuba.Total).Append(" mapped checks</td></tr>");
        if (r.SecureScore is { } score && score.Max > 0)
            b.Append("<tr><td>Microsoft Secure Score</td><td>").Append(Math.Round(score.Current)).Append(" of ").Append(Math.Round(score.Max)).Append(" points</td></tr>");
        b.Append("<tr><td>CIS Microsoft 365 Foundations</td><td>Not mapped yet</td></tr></tbody></table>");

        if (unknown.Count > 0)
        {
            b.Append("<h2>Couldn't be checked</h2><table><tbody>");
            foreach (var f in unknown)
                b.Append("<tr><td>").Append(E(f.Check.Title)).Append("</td><td>").Append(E(f.Detail)).Append("</td></tr>");
            b.Append("</tbody></table>");
        }

        b.Append("<footer>Read-only assessment: no settings were changed. Generated by Vigil365 from the tenant's configuration on ")
            .Append(E(date)).Append(". Baseline references are identifiers only; titles and guidance are written by Vigil365.</footer>");
        b.Append("</main></body></html>");
        return b.ToString();
    }
}
