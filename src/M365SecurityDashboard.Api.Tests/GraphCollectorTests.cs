using System.Net;
using System.Text;
using Azure.Core;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// A collection run against a scripted Graph: what it records when nothing
/// comes back, and how alerts that drop out of a filtered feed are resolved.
/// </summary>
public sealed class GraphCollectorTests
{
    private const string NonCompliantFilter = "complianceState ne 'compliant'";

    /// <summary>Answers by URL: the first responder whose key the decoded URL contains; otherwise an empty page.</summary>
    private sealed class ScriptedGraph(Dictionary<string, Func<HttpResponseMessage>> responders, HttpStatusCode? everything = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (everything is HttpStatusCode all) return Task.FromResult(new HttpResponseMessage(all) { Content = new StringContent("{}") });
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            var match = responders.FirstOrDefault(r => url.Contains(r.Key, StringComparison.Ordinal));
            return Task.FromResult(match.Value?.Invoke() ?? Json("""{"value":[]}"""));
        }
    }

    private sealed class FixedToken : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("token", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{}") };

    private static GraphCollector Collector(AppDbContext db, HttpMessageHandler graph)
    {
        var options = new GraphOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111", ClientId = "c", ClientSecret = "s", BaseUrl = "https://graph.test",
            ExchangeQuarantinePath = "/beta/quarantine", MailFlowIssuesPath = "/v1.0/mailflow",
        };
        var metrics = new GraphMetrics();
        var credentials = new TenantGraphCredentials(db, TestTenancy.For(TestTenancy.Default), Options.Create(options),
            new SecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance),
            Options.Create(new EditionOptions()));
        var client = new GraphApiClient(new HttpClient(graph), options, new FixedToken(), metrics);
        return new GraphCollector(db, client, Options.Create(options), credentials, metrics, NullLogger<GraphCollector>.Instance);
    }

    private static void SeedOpenDevices(AppDbContext db, params string[] ids)
    {
        foreach (var id in ids)
            db.SecurityAlerts.Add(new SecurityAlert
            {
                Service = M365ServiceArea.Intune, AlertType = "NonCompliantDevice", ExternalId = id, Severity = AlertSeverity.High,
                Title = id, DetectedAt = DateTimeOffset.UtcNow.AddDays(-1), LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
        db.SaveChanges();
    }

    private static async Task<bool> IsResolvedAsync(AppDbContext db, string externalId)
        => (await db.SecurityAlerts.AsNoTracking().SingleAsync(a => a.ExternalId == externalId)).IsResolved;

    [Fact]
    public async Task A_run_where_every_source_fails_is_failed_with_the_reason_and_records_no_trend()
    {
        using var db = TestAppDbContextFactory.Create();
        var run = await Collector(db, new ScriptedGraph(new(), everything: HttpStatusCode.Unauthorized)).CollectAsync(CancellationToken.None);

        Assert.Equal(CollectionStatus.Failed, run.Status);
        Assert.StartsWith("Every Graph source failed.", run.Error);
        Assert.Empty(await db.TrendSnapshots.ToListAsync()); // no all-zero Secure Score / MFA point
    }

    [Fact]
    public async Task A_device_that_drops_out_of_the_non_compliant_feed_is_resolved()
    {
        using var db = TestAppDbContextFactory.Create();
        SeedOpenDevices(db, "dev-1", "dev-2");
        var graph = new ScriptedGraph(new()
        {
            [NonCompliantFilter] = () => Json("""{"value":[{"id":"dev-2","deviceName":"PC2","complianceState":"noncompliant"}]}"""),
        });

        var run = await Collector(db, graph).CollectAsync(CancellationToken.None);

        Assert.Equal(CollectionStatus.Completed, run.Status);
        Assert.True(await IsResolvedAsync(db, "dev-1"));  // now compliant
        Assert.False(await IsResolvedAsync(db, "dev-2")); // still returned
    }

    [Fact]
    public async Task A_failed_source_resolves_nothing()
    {
        using var db = TestAppDbContextFactory.Create();
        SeedOpenDevices(db, "dev-1");
        var graph = new ScriptedGraph(new() { [NonCompliantFilter] = () => Status(HttpStatusCode.Forbidden) });

        await Collector(db, graph).CollectAsync(CancellationToken.None);

        Assert.False(await IsResolvedAsync(db, "dev-1"));
    }

    [Fact]
    public async Task A_truncated_read_resolves_nothing()
    {
        using var db = TestAppDbContextFactory.Create();
        SeedOpenDevices(db, "dev-1");
        var graph = new ScriptedGraph(new()
        {
            ["/page-2"] = () => Status(HttpStatusCode.ServiceUnavailable),
            [NonCompliantFilter] = () => Json("""{"value":[{"id":"dev-2","deviceName":"PC2"}],"@odata.nextLink":"https://graph.test/page-2"}"""),
        });

        await Collector(db, graph).CollectAsync(CancellationToken.None);

        Assert.False(await IsResolvedAsync(db, "dev-1")); // may be on the page that failed
        Assert.False(await IsResolvedAsync(db, "dev-2"));
    }

    [Fact]
    public async Task A_remediated_or_dismissed_risky_user_is_not_counted_as_risky()
    {
        using var db = TestAppDbContextFactory.Create();
        var graph = new ScriptedGraph(new()
        {
            ["/identityProtection/riskyUsers"] = () => Json("""
                {"value":[
                  {"id":"u1","userPrincipalName":"a@x.com","riskLevel":"high","riskState":"atRisk"},
                  {"id":"u2","userPrincipalName":"b@x.com","riskLevel":"high","riskState":"remediated"},
                  {"id":"u3","userPrincipalName":"c@x.com","riskLevel":"medium","riskState":"dismissed"}]}
                """),
        });

        await Collector(db, graph).CollectAsync(CancellationToken.None);

        Assert.False(await IsResolvedAsync(db, "u1"));
        Assert.True(await IsResolvedAsync(db, "u2"));
        Assert.True(await IsResolvedAsync(db, "u3"));
    }
}
