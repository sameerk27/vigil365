using System.Text;
using System.Text.RegularExpressions;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The digest PDF is written by hand rather than pulled from a library (the
/// obvious candidates carry licences that do not suit an MIT project). That
/// trade means the structure has to be verified here: a PDF whose xref byte
/// offsets are wrong still "generates" fine and then fails to open in every
/// reader, which is the worst possible failure mode for an emailed report.
/// </summary>
public class DigestPdfRendererTests
{
    private static DigestBuilder.Digest Sample(
        IReadOnlyList<DigestBuilder.Metric>? metrics = null,
        IReadOnlyList<DigestBuilder.TopAlert>? alerts = null) =>
        new(
            Subject: "Vigil365 digest",
            HtmlBody: "<p>ignored by the PDF path</p>",
            Csv: null,
            GeneratedAt: new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero),
            Metrics: metrics ?? [new DigestBuilder.Metric("Secure Score", "51%", -2.4, "pts", true)],
            TopAlerts: alerts ?? [new DigestBuilder.TopAlert("Privileged role assigned", "high", "count >= 1", 3, DateTimeOffset.UtcNow)],
            HasData: true);

    private static string Ascii(byte[] pdf) => Encoding.ASCII.GetString(pdf);

    [Fact]
    public void Render_ProducesAPdfHeaderAndTrailer()
    {
        var text = Ascii(new DigestPdfRenderer().Render(Sample()));
        Assert.StartsWith("%PDF-", text);
        Assert.Contains("%%EOF", text);
        Assert.Contains("/Type /Catalog", text);
    }

    /// <summary>
    /// The real correctness test: every xref entry must be the exact byte offset
    /// of its object. Readers seek by these; if they drift the file is rejected.
    /// </summary>
    [Fact]
    public void Render_XrefOffsetsPointAtTheirObjects()
    {
        var pdf = new DigestPdfRenderer().Render(Sample());
        var text = Ascii(pdf);

        // Locate the table via startxref rather than searching for "xref" — the
        // word "startxref" itself contains it, and appears later in the file.
        var startxref = Regex.Match(text, @"startxref\s+(\d+)");
        Assert.True(startxref.Success, "PDF has no startxref");
        var xrefIndex = int.Parse(startxref.Groups[1].Value);
        Assert.True(xrefIndex > 0 && xrefIndex < pdf.Length, "startxref points outside the file");

        // Entries look like "0000000123 00000 n" — skip the leading free entry.
        var entries = Regex.Matches(text[xrefIndex..], @"^(\d{10}) 00000 n", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();

        Assert.NotEmpty(entries);

        for (var i = 0; i < entries.Count; i++)
        {
            var offset = entries[i];
            Assert.InRange(offset, 0, pdf.Length - 1);

            // At that byte offset the file must literally begin object i+1.
            var expected = $"{i + 1} 0 obj";
            var actual = Encoding.ASCII.GetString(pdf, offset, Math.Min(expected.Length, pdf.Length - offset));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Render_StartxrefPointsAtTheXrefTable()
    {
        var pdf = new DigestPdfRenderer().Render(Sample());
        var text = Ascii(pdf);

        var match = Regex.Match(text, @"startxref\s+(\d+)");
        Assert.True(match.Success, "PDF has no startxref");

        var offset = int.Parse(match.Groups[1].Value);
        Assert.InRange(offset, 0, pdf.Length - 4);
        Assert.Equal("xref", Encoding.ASCII.GetString(pdf, offset, 4));
    }

    [Fact]
    public void Render_EscapesParenthesesSoTextOperatorsCannotBreak()
    {
        // An unescaped ) would terminate the PDF string early and corrupt the page.
        var digest = Sample(alerts: [new DigestBuilder.TopAlert(
            "Odd (policy) name \\ here", "high", "value >= 1 (spike)", 1, DateTimeOffset.UtcNow)]);
        var text = Ascii(new DigestPdfRenderer().Render(digest));

        Assert.Contains(@"Odd \(policy\) name \\ here", text);
    }

    [Fact]
    public void Render_ReplacesNonAsciiSoTheAsciiEncodingCannotMangleIt()
    {
        // The writer emits ASCII; em dashes and arrows must be folded, not dropped
        // into '?' which would look like corruption in the report.
        var digest = Sample(alerts: [new DigestBuilder.TopAlert(
            "Role → admin", "high", "score — dropped ▼", 1, DateTimeOffset.UtcNow)]);
        var text = Ascii(new DigestPdfRenderer().Render(digest));

        Assert.Contains("Role > admin", text);
        Assert.Contains("score - dropped v", text);
        Assert.DoesNotContain("?", text);
    }

    [Fact]
    public void Render_EmptyDigestStillProducesAValidPdf()
    {
        // A tenant with no data must still get an openable report, not a broken file.
        var digest = Sample(metrics: [], alerts: []);
        var pdf = new DigestPdfRenderer().Render(digest);
        var text = Ascii(pdf);

        Assert.StartsWith("%PDF-", text);
        Assert.Contains("%%EOF", text);
        Assert.Contains("No posture snapshot captured yet.", text);
        Assert.Contains("No open alerts.", text);
    }

    [Fact]
    public void Render_ManyAlertsDoesNotOverflowThePage()
    {
        // The writer stops at the bottom margin; it must not run off the page or
        // emit negative coordinates.
        var alerts = Enumerable.Range(0, 200)
            .Select(i => new DigestBuilder.TopAlert($"Policy {i}", "low", "c", i, DateTimeOffset.UtcNow))
            .ToList();
        var pdf = new DigestPdfRenderer().Render(Sample(alerts: alerts));
        var text = Ascii(pdf);

        Assert.Contains("%%EOF", text);
        Assert.DoesNotMatch(new Regex(@"1 0 0 1 50 -\d+ Tm"), text);
    }
}
