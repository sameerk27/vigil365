using M365SecurityDashboard.Api.Services.Common;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>Values headed into size-limited columns must be cut, never rejected by the database.</summary>
public class TextLimitsTests
{
    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abc", 0, "")]
    public void Truncate_cuts_to_the_limit(string s, int max, string expected) =>
        Assert.Equal(expected, TextLimits.Truncate(s, max));

    [Fact]
    public void TruncateOrNull_keeps_null_and_empty_as_they_are()
    {
        Assert.Null(TextLimits.TruncateOrNull(null, 3));
        Assert.Equal("", TextLimits.TruncateOrNull("", 3));
        Assert.Equal("abc", TextLimits.TruncateOrNull("abcdef", 3));
    }

    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 4, "abc…")]
    [InlineData("abcdef", 1, "…")]
    [InlineData("abcdef", 0, "")]
    public void Ellipsize_cuts_one_short_and_marks_the_cut(string s, int max, string expected) =>
        Assert.Equal(expected, TextLimits.Ellipsize(s, max));
}
