using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services.PowerPlatform;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The optional Power Platform inventory (plan phase D): a tenant that hasn't given the
/// app the Power Platform reader role sees how to turn it on, not an error; a tenant
/// that has gets its environments, apps and flows with the risky ones flagged; one
/// environment failing never hides the rest; and the client only ever sends GET.
/// Response shapes follow Microsoft's REST reference for environments and apps. The
/// cloud-flow shape isn't documented there, so these samples follow the .NET SDK model.
/// </summary>
public sealed class PowerPlatformInventoryTests
{
    private const string Base = "https://pp.test";

    private static GraphOptions Options(int maxEnvironments = 50) => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111", ClientId = "c", ClientSecret = "s",
        PowerPlatformApiBaseUrl = Base, PowerPlatformMaxEnvironments = maxEnvironments,
    };

    private sealed class FixedToken : TokenCredential
    {
        public readonly List<string> Scopes = [];
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) { Scopes.AddRange(c.Scopes); return new("token", DateTimeOffset.MaxValue); }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    private sealed class FailingToken(string message) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => throw new AuthenticationFailedException(message);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => throw new AuthenticationFailedException(message);
    }

    /// <summary>A scripted Power Platform API that records every request. Unscripted URLs answer an empty list.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, bool HasToken)> Requests = [];
        public Func<string, HttpResponseMessage?> Respond { get; set; } = _ => null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Add((request.Method, url, request.Headers.Authorization is not null));
            return Task.FromResult(Respond(url) ?? Json("""{"value":[]}"""));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static PowerPlatformInventoryService Service(FakeApi api, GraphOptions? options = null, TokenCredential? token = null, IMemoryCache? cache = null)
    {
        var o = options ?? Options();
        var client = new PowerPlatformApiClient(new HttpClient(api), o, token ?? new FixedToken());
        return new PowerPlatformInventoryService(client, new TenantContext(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            cache ?? new MemoryCache(new MemoryCacheOptions()), Microsoft.Extensions.Options.Options.Create(o),
            NullLogger<PowerPlatformInventoryService>.Instance);
    }

    private const string Environments = """
        {"value":[
          {"id":"env-prod","displayName":"Contoso (default)","type":"Default","state":"Ready","azureRegion":"europe","dataverseId":"org-1","createdDateTime":"2024-02-01T10:00:00Z"},
          {"id":"env-trial","displayName":"Sam's trial","type":"Trial","state":"Ready","geo":"unitedstates"}
        ]}
        """;

    private const string ProdApps = """
        {"value":[
          {"id":"/providers/Microsoft.PowerApps/apps/app-1","name":"app-1","type":"Microsoft.PowerApps/apps","properties":{
            "displayName":"Expense approvals","createdTime":"2025-01-10T09:00:00Z","lastModifiedTime":"2026-10-01T09:00:00Z",
            "owner":{"displayName":"Priya Shah","userPrincipalName":"priya@contoso.com"},
            "sharedUsersCount":120,"sharedGroupsCount":1,"bypassConsent":true,
            "connectionReferences":[
              {"id":"/providers/microsoft.powerapps/apis/shared_sharepointonline","displayName":"SharePoint"},
              {"id":"/providers/microsoft.powerapps/apis/shared_custom-erp","displayName":"Contoso ERP","isCustomApiConnection":true,"isOnPremiseConnection":true}
            ]}},
          {"id":"/providers/Microsoft.PowerApps/apps/app-2","name":"app-2","properties":{"displayName":"Room booking","sharedUsersCount":3,"sharedGroupsCount":0,"connectionReferences":[]}}
        ]}
        """;

    private const string TrialApps = """
        {"value":[{"name":"app-3","properties":{"displayName":"Dropbox uploader","connectionReferences":{"x":{"id":"/providers/microsoft.powerapps/apis/shared_dropbox","displayName":"Dropbox"}}}}]}
        """;

    // definition = the workflow's client data, a JSON string.
    private static readonly string ProdFlows = $$"""
        {"value":[
          {"workflowId":"f-1","name":"Sync invoices to partner API","stateCode":1,"createdOn":"2026-09-01T08:00:00Z","modifiedOn":"2026-10-18T08:00:00Z",
           "definition":{{JsonSerializer.Serialize("""{"properties":{"connectionReferences":{"shared_webcontents":{"api":{"name":"shared_webcontents","displayName":"HTTP with Microsoft Entra ID"}},"shared_office365":{"api":{"name":"shared_office365","displayName":"Office 365 Outlook"}}}}}""")}}},
          {"workflowId":"f-2","name":"Old reminder","stateCode":2,"definition":"not json"}
        ]}
        """;

    private static Func<string, HttpResponseMessage?> Tenant(string? prodApps = ProdApps, string? trialApps = TrialApps, string? prodFlows = null) => url =>
    {
        if (url.Contains("/environmentmanagement/environments")) return Json(Environments);
        if (url.Contains("/powerapps/environments/env-prod/apps")) return prodApps is null ? Status(HttpStatusCode.InternalServerError, """{"error":{"code":"x","message":"boom"}}""") : Json(prodApps);
        if (url.Contains("/powerapps/environments/env-trial/apps")) return trialApps is null ? Status(HttpStatusCode.Forbidden) : Json(trialApps);
        if (url.Contains("/powerautomate/environments/env-prod/cloudFlows")) return Json(prodFlows ?? ProdFlows);
        return null;
    };

    // ── Not turned on ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Without_the_reader_role_the_inventory_says_NotEnabled_not_error(HttpStatusCode code)
    {
        var api = new FakeApi { Respond = _ => Status(code, """{"error":{"code":"Forbidden","message":"The caller has no role assignment."}}""") };

        var r = await Service(api).ReadAsync(CancellationToken.None);

        Assert.Equal(PowerPlatformInventoryResult.NotEnabled, r.Status);
        Assert.Equal("RoleMissing", r.Reason);
        Assert.Single(api.Requests); // stops at the environment list
    }

    [Fact]
    public async Task A_tenant_without_the_Power_Platform_API_registered_says_NotEnabled_and_sends_nothing()
    {
        var api = new FakeApi();
        var token = new FailingToken("AADSTS500011: The resource principal named https://api.powerplatform.com was not found in the tenant.");

        var r = await Service(api, token: token).ReadAsync(CancellationToken.None);

        Assert.Equal(PowerPlatformInventoryResult.NotEnabled, r.Status);
        Assert.Equal("ApiNotInTenant", r.Reason);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Another_environment_list_failure_is_an_error_with_Microsofts_message()
    {
        var api = new FakeApi { Respond = _ => Status(HttpStatusCode.BadRequest, """{"error":{"code":"BadRequest","message":"api-version is not supported"}}""") };

        var r = await Service(api).ReadAsync(CancellationToken.None);

        Assert.Equal(PowerPlatformInventoryResult.Error, r.Status);
        Assert.Contains("api-version is not supported", r.Reason);
    }

    // ── Turned on ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reads_environments_apps_and_flows_and_flags_the_risky_ones()
    {
        var api = new FakeApi { Respond = Tenant() };
        var token = new FixedToken();

        var r = await Service(api, token: token).ReadAsync(CancellationToken.None);

        Assert.Equal(PowerPlatformInventoryResult.Ready, r.Status);
        Assert.All(token.Scopes, s => Assert.Equal("https://pp.test/.default", s));

        var prod = Assert.Single(r.Environments, e => e.Id == "env-prod");
        Assert.True(prod.HasDataverse);
        Assert.Equal((2, 2), (prod.Apps, prod.Flows));
        var trial = Assert.Single(r.Environments, e => e.Id == "env-trial");
        Assert.False(trial.HasDataverse);
        Assert.Equal("unitedstates", trial.Region);
        // Flows are only asked for where Dataverse exists (elsewhere Microsoft answers 404).
        Assert.DoesNotContain(api.Requests, q => q.Url.Contains("env-trial/cloudFlows"));

        var expense = Assert.Single(r.Apps, a => a.Name == "Expense approvals");
        Assert.Equal("Priya Shah", expense.Owner);
        Assert.Equal("Contoso (default)", expense.EnvironmentName);
        Assert.Equal(["SharePoint", "Contoso ERP"], expense.Connectors);
        Assert.Equal(
            [PowerPlatformInventoryService.ConsentBypassedFlag, PowerPlatformInventoryService.CustomConnectorFlag,
             PowerPlatformInventoryService.GatewayFlag, "Shared with 120 people", PowerPlatformInventoryService.SharedWithGroupFlag],
            expense.Flags.Select(f => f.Label));
        Assert.Empty(Assert.Single(r.Apps, a => a.Name == "Room booking").Flags);
        // Connection references as an object keyed by id are read too.
        Assert.Contains(Assert.Single(r.Apps, a => a.Name == "Dropbox uploader").Flags, f => f.Label == "High-risk connector: Dropbox");

        var sync = Assert.Single(r.Flows, f => f.Id == "f-1");
        Assert.Equal("On", sync.State);
        Assert.Equal(["HTTP with Microsoft Entra ID", "Office 365 Outlook"], sync.Connectors);
        Assert.Equal("High-risk connector: HTTP with Microsoft Entra ID", Assert.Single(sync.Flags).Label);
        var old = Assert.Single(r.Flows, f => f.Id == "f-2");
        Assert.Equal("Suspended", old.State);
        Assert.Empty(old.Connectors); // an unreadable definition costs the connectors, not the flow

        Assert.Equal(new PowerPlatformSummary(Environments: 2, EnvironmentsRead: 2, EnvironmentsSkipped: 0, Apps: 3, Flows: 2,
            ConsentBypassed: 1, HighRiskConnector: 2, WidelyShared: 1, CustomConnector: 1), r.Summary);
    }

    [Fact]
    public async Task One_environment_failing_marks_it_and_keeps_the_rest()
    {
        var api = new FakeApi { Respond = Tenant(prodApps: null, trialApps: null) };

        var r = await Service(api).ReadAsync(CancellationToken.None);

        Assert.Equal(PowerPlatformInventoryResult.Partial, r.Status);
        Assert.Contains("apps couldn't be read (error 500)", Assert.Single(r.Environments, e => e.Id == "env-prod").Problem);
        Assert.Contains("access refused", Assert.Single(r.Environments, e => e.Id == "env-trial").Problem);
        Assert.Equal(2, r.Flows.Count); // the prod flows still came back
        Assert.Equal(0, r.Summary.EnvironmentsRead);
    }

    [Fact]
    public async Task No_flows_is_204_or_404_and_neither_is_a_problem()
    {
        foreach (var code in new[] { HttpStatusCode.NoContent, HttpStatusCode.NotFound })
        {
            var api = new FakeApi
            {
                Respond = url => url.Contains("/cloudFlows") ? Status(code) : Tenant()(url),
            };

            var r = await Service(api).ReadAsync(CancellationToken.None);

            Assert.Equal(PowerPlatformInventoryResult.Ready, r.Status);
            Assert.Empty(r.Flows);
        }
    }

    [Fact]
    public async Task Environments_past_the_cap_are_counted_as_skipped()
    {
        var api = new FakeApi { Respond = Tenant() };

        var r = await Service(api, Options(maxEnvironments: 1)).ReadAsync(CancellationToken.None);

        Assert.Equal(PowerPlatformInventoryResult.Partial, r.Status);
        Assert.Equal((2, 1), (r.Summary.Environments, r.Summary.EnvironmentsSkipped));
        Assert.Single(r.Environments);
        Assert.DoesNotContain(api.Requests, q => q.Url.Contains("env-trial"));
    }

    // ── Paging, caching, read-only ─────────────────────────────────────────────

    [Fact]
    public async Task Next_links_are_followed_on_the_Power_Platform_host_only()
    {
        var api = new FakeApi
        {
            Respond = url =>
            {
                if (url.Contains("/environmentmanagement/environments")) return Json("""{"value":[{"id":"env-a","displayName":"A"}]}""");
                if (url.Contains("$skiptoken=page2")) return Json("""{"value":[{"name":"app-2","properties":{"displayName":"Two"}}],"nextLink":"https://evil.test/steal?$skiptoken=page3"}""");
                if (url.Contains("/powerapps/environments/env-a/apps")) return Json("""{"value":[{"name":"app-1","properties":{"displayName":"One"}}],"nextLink":"https://pp.test/powerapps/environments/env-a/apps?api-version=2024-10-01&$skiptoken=page2"}""");
                return null;
            },
        };

        var r = await Service(api).ReadAsync(CancellationToken.None);

        Assert.Equal(["One", "Two"], r.Apps.Select(a => a.Name));
        Assert.All(api.Requests, q => Assert.StartsWith("https://pp.test/", q.Url)); // the token never left the host
    }

    [Fact]
    public async Task The_inventory_is_cached_until_refresh_is_asked_for()
    {
        var api = new FakeApi { Respond = Tenant() };
        var service = Service(api);

        await service.GetAsync(refresh: false, CancellationToken.None);
        var afterFirst = api.Requests.Count;
        await service.GetAsync(refresh: false, CancellationToken.None);
        Assert.Equal(afterFirst, api.Requests.Count);

        await service.GetAsync(refresh: true, CancellationToken.None);
        Assert.Equal(afterFirst * 2, api.Requests.Count);
    }

    [Fact]
    public async Task Every_request_is_a_GET_with_a_token()
    {
        var api = new FakeApi { Respond = Tenant() };

        await Service(api).ReadAsync(CancellationToken.None);

        Assert.NotEmpty(api.Requests);
        Assert.All(api.Requests, q => { Assert.Equal(HttpMethod.Get, q.Method); Assert.True(q.HasToken); });
    }

    [Fact]
    public void The_Power_Platform_code_has_no_HTTP_method_but_GET()
    {
        var dir = Path.Combine(ApiDir(), "Services", "PowerPlatform");
        var methods = Directory.EnumerateFiles(dir, "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"HttpMethod\.(\w+)").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.Equal(["Get"], methods);
    }

    // ── Parsing details ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0", "Off")]
    [InlineData("1", "On")]
    [InlineData("2", "Suspended")]
    [InlineData("\"Published\"", "On")]
    [InlineData("\"Draft\"", "Off")]
    [InlineData("null", "Unknown")]
    public void Flow_state_follows_Microsofts_WorkflowStateCode(string json, string expected) =>
        Assert.Equal(expected, PowerPlatformInventoryService.FlowState(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("HTTP", true)]
    [InlineData("HTTP with Microsoft Entra ID", true)]
    [InlineData("shared_sftpwithssh", true)]
    [InlineData("gmail", true)]
    [InlineData("SharePoint", false)]
    [InlineData("Dropbox Business", false)] // no trailing * on Dropbox: exact match only
    public void High_risk_matching_uses_the_configured_list(string connector, bool risky) =>
        Assert.Equal(risky, PowerPlatformInventoryService.HighRisk([connector], Options()).Any());

    [Fact]
    public void A_flow_definition_given_as_an_object_is_read_too()
    {
        var def = JsonDocument.Parse("""{"properties":{"connectionReferences":{"shared_ftp":{"api":{"name":"shared_ftp"}}}}}""").RootElement;
        Assert.Equal(["shared_ftp"], PowerPlatformInventoryService.FlowConnectors(def));
    }

    private static string ApiDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var api = Path.Combine(d.FullName, "src", "M365SecurityDashboard.Api");
            if (Directory.Exists(api)) return api;
        }
        throw new DirectoryNotFoundException("src/M365SecurityDashboard.Api not found above the test output folder.");
    }
}
