using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Locks the CSV export contract. Every exported field carries tenant-controlled
/// text (alert titles, display names, audit actors), so a regression here is a
/// live spreadsheet-formula injection in a security product's own export.
/// </summary>
public class CsvSanitizerTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"click\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A9)")]
    [InlineData("\tleading-tab")]
    [InlineData("\rleading-cr")]
    public void Neutralize_PrefixesFormulaTriggers(string dangerous)
    {
        var result = CsvSanitizer.Neutralize(dangerous);
        Assert.StartsWith("'", result);
        Assert.Equal("'" + dangerous, result);
    }

    [Theory]
    [InlineData("Risky user detected")]
    [InlineData("user@contoso.com")]      // @ only matters in first position
    [InlineData("Score dropped 51-38")]   // - only matters in first position
    [InlineData("")]
    public void Neutralize_LeavesSafeValuesUntouched(string safe)
    {
        Assert.Equal(safe, CsvSanitizer.Neutralize(safe));
    }

    [Fact]
    public void Neutralize_NullBecomesEmpty() => Assert.Equal("", CsvSanitizer.Neutralize(null));

    [Fact]
    public void Field_AppliesFormulaGuardAndRfc4180Quoting()
    {
        // Contains a comma AND starts with '=' — must be both neutralised and quoted.
        Assert.Equal("\"'=cmd,evil\"", CsvSanitizer.Field("=cmd,evil"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    public void Field_QuotesOnlyWhenRequired(string input, string expected)
    {
        Assert.Equal(expected, CsvSanitizer.Field(input));
    }
}
