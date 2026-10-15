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
    // Seen on the first live onboarding: consent succeeded, and the immediate test got this.
    // The request-id GUIDs deliberately contain "403" and "429" to prove the match wins first.
    private const string NotProvisioned =
        "401 Unauthorized: {\"error\":{\"code\":\"Authorization_IdentityNotFound\",\"message\":\"The identity of the calling " +
        "application could not be established.\",\"innerError\":{\"date\":\"2026-10-04T07:47:08\",\"request-id\":\"4034aa29-c5b5-4296-9ebf-23cabbd88719\"}}}";

    [Fact]
    public void Identity_not_found_right_after_consent_is_recognised_as_a_wait_not_a_failure()
    {
        Assert.True(GraphErrorHint.IsNotYetProvisioned(NotProvisioned));
        Assert.False(GraphErrorHint.IsNotYetProvisioned("401 Unauthorized: invalid_client"));
        Assert.False(GraphErrorHint.IsNotYetProvisioned(null));
    }

    [Fact]
    public void Identity_not_found_is_not_described_as_an_expired_secret_or_a_denied_permission()
    {
        foreach (var msg in new[] { GraphErrorHint.Describe(NotProvisioned), GraphErrorHint.DescribeOrNull(NotProvisioned) })
        {
            Assert.Equal(GraphErrorHint.NotYetProvisionedMessage, msg);
            Assert.DoesNotContain("expired", msg!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Permission denied", msg!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_plain_401_still_points_at_the_credentials()
        => Assert.Contains("expired", GraphErrorHint.DescribeOrNull("401 Unauthorized: invalid_client")!, StringComparison.OrdinalIgnoreCase);

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
