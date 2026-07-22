using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// A collector failure is the moment the product most needs to be clear: the
/// admin sees "1 source failed" and must learn what to do about it. These lock
/// the translation from Graph's raw JSON to an actionable sentence.
/// </summary>
public class GraphErrorHintTests
{
    // The exact shape Graph returns when an application permission is missing.
    private const string Real403 =
        "403 Forbidden: {\"error\":{\"code\":\"accessDenied\",\"message\":\"Caller does not have required permissions for this API\"}}";

    [Fact]
    public void Describe_403WithKnownSource_NamesTheMissingPermission()
    {
        var msg = GraphErrorHint.Describe(Real403, "SharePoint sharing posture");
        Assert.Contains("SharePointTenantSettings.Read.All", msg);
        Assert.Contains("admin consent", msg, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", msg); // no raw JSON leaks through
    }

    [Fact]
    public void Describe_403WithUnknownSource_StillExplainsItIsAPermissionProblem()
    {
        var msg = GraphErrorHint.Describe(Real403, "Some Future Source");
        Assert.Contains("Permission denied", msg);
        Assert.DoesNotContain("{", msg);
    }

    [Theory]
    [InlineData("Risky users", "IdentityRiskyUser.Read.All")]
    [InlineData("Defender alerts", "SecurityAlert.Read.All")]
    [InlineData("Non-compliant devices", "DeviceManagementManagedDevices.Read.All")]
    [InlineData("Tenant audit events", "AuditLog.Read.All")]
    [InlineData("SharePoint sharing posture", "SharePointTenantSettings.Read.All")]
    public void PermissionFor_MapsEveryCollectorSource(string source, string expected)
        => Assert.Equal(expected, GraphErrorHint.PermissionFor(source));

    [Fact]
    public void PermissionFor_UnknownSourceIsNull()
        => Assert.Null(GraphErrorHint.PermissionFor("Not A Source"));

    [Theory]
    [InlineData("429 TooManyRequests", "throttl")]
    [InlineData("401 Unauthorized", "credentials")]
    [InlineData("404 NotFound", "licensed")]
    public void Describe_RecognisesOtherCommonFailures(string raw, string expectedFragment)
        => Assert.Contains(expectedFragment, GraphErrorHint.Describe(raw), System.StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Describe_UnrecognisedFailureKeepsOriginalDetail()
    {
        // Never hide detail we cannot improve on.
        const string odd = "Socket closed unexpectedly while reading response";
        Assert.Equal(odd, GraphErrorHint.Describe(odd));
    }

    [Fact]
    public void DescribeOrNull_ReturnsNullForUnrecognised_SoCallersKeepTheirSafeMessage()
    {
        // Endpoint variant must never echo an unrecognised exception into HTTP.
        Assert.Null(GraphErrorHint.DescribeOrNull("Object reference not set to an instance of an object"));
    }

    [Fact]
    public void DescribeOrNull_403UsesTheCallerSuppliedPermission()
    {
        var msg = GraphErrorHint.DescribeOrNull(Real403, "SecurityEvents.Read.All");
        Assert.Contains("SecurityEvents.Read.All", msg);
    }

    [Fact]
    public void Describe_TrimsVeryLongUnrecognisedMessages()
        => Assert.Equal(50, GraphErrorHint.Describe(new string('x', 500), null, 50).Length);
}
