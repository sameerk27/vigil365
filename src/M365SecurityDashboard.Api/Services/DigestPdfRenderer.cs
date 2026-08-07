using System.Globalization;
using System.Text;

namespace M365SecurityDashboard.Api.Services;

public sealed class DigestPdfRenderer
{
    public byte[] Render(DigestBuilder.Digest digest)
    {
        var lines = new List<(string Text, int Size, bool Bold)>
        {
            ("Vigil365 - Executive Security Digest", 18, true),
            ($"Generated {digest.GeneratedAt:yyyy-MM-dd HH:mm} UTC", 10, false),
            ("", 10, false),
            ("Posture", 13, true),
        };

        if (digest.Metrics.Count == 0)
        {
            lines.Add(("No posture snapshot captured yet.", 10, false));
        }
        else
        {
            foreach (var m in digest.Metrics)
            {
                var delta = m.Delta is { } d
                    ? d.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(m.DeltaLabel) ? "" : " " + m.DeltaLabel)
                    : "n/a";
                lines.Add(($"  {m.Label}: {m.Value} ({delta})", 10, false));
            }
        }

        lines.Add(("", 10, false));
        lines.Add(("Top open alerts", 13, true));
        if (digest.TopAlerts.Count == 0)
        {
            lines.Add(("No open alerts.", 10, false));
        }
        else
        {
            foreach (var alert in digest.TopAlerts)
                lines.Add(($"  [{alert.Severity.ToUpperInvariant()}] {alert.PolicyName} - {alert.Condition}", 10, false));
        }

        lines.Add(("", 10, false));
        lines.Add(("Read-only monitoring: this report did not change the Microsoft 365 tenant.", 9, false));

        return SimplePdf(lines);
    }

    private static byte[] SimplePdf(IReadOnlyList<(string Text, int Size, bool Bold)> lines)
    {
        var objects = new List<string>();
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");

        var content = new StringBuilder();
        content.AppendLine("BT");
        var y = 750;
        foreach (var line in lines)
        {
            if (y < 48) break;
            var font = line.Bold ? "F2" : "F1";
            content.AppendLine($"/{font} {line.Size} Tf");
            content.AppendLine($"1 0 0 1 50 {y} Tm ({Escape(line.Text)}) Tj");
            y -= Math.Max(14, line.Size + 5);
        }
        content.AppendLine("ET");
        var stream = content.ToString();
        objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");

        var pdf = new StringBuilder();
        var offsets = new List<int> { 0 };
        pdf.AppendLine("%PDF-1.4");
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.AppendLine($"{i + 1} 0 obj");
            pdf.AppendLine(objects[i]);
            pdf.AppendLine("endobj");
        }

        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.AppendLine("xref");
        pdf.AppendLine($"0 {objects.Count + 1}");
        pdf.AppendLine("0000000000 65535 f ");
        foreach (var offset in offsets.Skip(1))
            pdf.AppendLine($"{offset:0000000000} 00000 n ");
        pdf.AppendLine("trailer");
        pdf.AppendLine($"<< /Size {objects.Count + 1} /Root 1 0 R >>");
        pdf.AppendLine("startxref");
        pdf.AppendLine(xref.ToString(CultureInfo.InvariantCulture));
        pdf.AppendLine("%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string Escape(string s)
    {
        var clean = s.Replace('\u2014', '-').Replace('\u2013', '-').Replace('\u2192', '>')
            .Replace('\u25b2', '^').Replace('\u25bc', 'v');
        return clean.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }
}
