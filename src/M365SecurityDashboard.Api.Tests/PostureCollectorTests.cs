using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Azure.Core;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using M365SecurityDashboard.Api.Services.Posture;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Reading posture from Graph: a short read is never taken for the whole list (a missing
/// licence page would turn owned features into "needs another licence"), and Secure Score
/// is one page, not the score history.
/// </summary>
public sealed class PostureCollectorTests
{
    private const string GraphBase = "https://graph.test";
    private const string E3 = """{"skuPartNumber":"SPE_E3","capabilityStatus":"Enabled","prepaidUnits":{"enabled":10},"consumedUnits":8,"servicePlans":[]}""";

    private sealed class FixedToken : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("token", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    private sealed class FakeGraph(Func<string, HttpResponseMessage> respond, ConcurrentBag<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            seen.Add(url);
            return Task.FromResult(respond(url));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Code(HttpStatusCode code) => new(code) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    private static string Page(string items, string? next = null) =>
        next is null ? $$"""{"value":[{{items}}]}""" : $$"""{"value":[{{items}}],"@odata.nextLink":"{{GraphBase}}{{next}}"}""";

    private static async Task<(PostureSnapshot Snapshot, IReadOnlyCollection<string> Seen)> Collect(Func<string, HttpResponseMessage> respond)
    {
        var seen = new ConcurrentBag<string>();
        var graph = new GraphApiClient(new HttpClient(new FakeGraph(respond, seen)),
            new GraphOptions { TenantId = "11111111-1111-1111-1111-111111111111", ClientId = "c", ClientSecret = "s", BaseUrl = GraphBase },
            new FixedToken(), new GraphMetrics());
        var snapshot = await new PostureCollector(graph, NullLogger<PostureCollector>.Instance).CollectAsync(PostureCatalog.Default, CancellationToken.None);
        return (snapshot, seen);
    }

    [Fact]
    public async Task A_licence_list_cut_short_is_unknown_not_unlicensed()
    {
        var (s, _) = await Collect(url =>
            url.Contains("subscribedSkus?$skiptoken=2") ? Code(HttpStatusCode.InternalServerError)
            : url.Contains("subscribedSkus") ? Json(Page(E3, "/v1.0/subscribedSkus?$skiptoken=2"))
            : Json(Page("")));

        Assert.Null(s.Skus.Value);
        Assert.Contains("Only some of the tenant's licences", s.Skus.Error);

        var result = PostureEvaluator.Evaluate(s, PostureCatalog.Default);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain(result.Findings, f => f.Status == PostureStatus.NotLicensed);
    }

    [Fact]
    public async Task An_intune_list_cut_short_is_not_a_count()
    {
        var (s, _) = await Collect(url =>
            url.Contains("deviceCompliancePolicies?$skiptoken=2") ? Code(HttpStatusCode.InternalServerError)
            : url.Contains("deviceCompliancePolicies") ? Json(Page("""{"id":"a"}""", "/v1.0/deviceManagement/deviceCompliancePolicies?$skiptoken=2"))
            : Json(Page("")));

        Assert.False(s.IntuneCompliancePolicies.Read);
        Assert.Contains("Only some Intune compliance policies", s.IntuneCompliancePolicies.Error);
    }

    [Fact]
    public async Task Secure_score_reads_the_latest_page_only()
    {
        var (s, seen) = await Collect(url =>
            url.Contains("secureScores") ? Json(Page("""{"currentScore":42.5,"maxScore":100}""", "/v1.0/security/secureScores?$top=1&$skip=1"))
            : Json(Page("")));

        Assert.Equal((42.5, 100d), s.SecureScore.Value);
        Assert.Single(seen, u => u.Contains("secureScores"));
    }

    [Fact]
    public async Task One_failed_read_leaves_the_others_read()
    {
        var (s, _) = await Collect(url =>
            url.Contains("conditionalAccess") ? Code(HttpStatusCode.Forbidden)
            : url.Contains("subscribedSkus") ? Json(Page(E3))
            : Json(Page("")));

        Assert.False(s.ConditionalAccess.Read);
        Assert.True(s.Skus.Read);
        Assert.True(s.MfaRegistration.Read);
    }
}
