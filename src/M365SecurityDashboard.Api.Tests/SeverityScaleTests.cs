using M365SecurityDashboard.Api.Services.Common;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The digest and the notification filters must order severities the same way; the only
/// intended difference is where an unrecognised severity lands.
/// </summary>
public class SeverityScaleTests
{
    [Theory]
    [InlineData("critical", 4)]
    [InlineData("High", 3)]
    [InlineData("MEDIUM", 2)]
    [InlineData("low", 1)]
    [InlineData("informational", 0)]
    public void Ranks_known_severities_ignoring_case(string severity, int expected) =>
        Assert.Equal(expected, SeverityScale.Rank(severity));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknownFutureValue")]
    public void Unknown_ranks_as_informational_by_default(string? severity) =>
        Assert.Equal(SeverityScale.Informational, SeverityScale.Rank(severity));

    [Fact]
    public void Notification_filters_can_rank_unknown_as_low_so_it_is_still_delivered() =>
        Assert.Equal(SeverityScale.Low, SeverityScale.Rank("unknownFutureValue", unknownAs: SeverityScale.Low));

    [Theory]
    [InlineData("Critical", "dc2626")]
    [InlineData("high", "ea580c")]
    [InlineData("medium", "d97706")]
    [InlineData("low", "2563eb")]
    [InlineData(null, "6b7280")]
    [InlineData("informational", "6b7280")]
    public void Maps_severity_to_card_colour(string? severity, string hex) =>
        Assert.Equal(hex, SeverityScale.HexColor(severity));
}
