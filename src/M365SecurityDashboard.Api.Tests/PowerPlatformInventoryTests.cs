using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using M365SecurityDashboard.Api.Services.PowerPlatform;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The Power Platform inventory (plan phase D). The browser reads Microsoft's inventory
/// with the signed-in person's own access; the server turns it into environments, apps,
/// flows and agents, flags the ones that need a look, and names owners through Graph.
/// Row shapes follow Microsoft's inventory schema, the Copilot Studio agent inventory
/// schema and the "Get AdminApps" response.
/// </summary>
public sealed class PowerPlatformInventoryTests
{
    private static GraphOptions Options() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111", ClientId = "c", ClientSecret = "s", BaseUrl = "https://graph.test",
    };

    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private const string Priya = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Lena = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Gone = "aaaaaaaa-0000-0000-0000-000000000003";

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<JsonElement> Resources() =>
    [
        J("""{"name":"env-prod","type":"microsoft.powerplatform/environments","location":"europe","properties":{"displayName":"Contoso (default)","environmentType":"Default","isManaged":false}}"""),
        J("""{"name":"env-fin","type":"microsoft.powerplatform/environments","location":"europe","properties":{"displayName":"Finance","environmentType":"Production","isManaged":true}}"""),
        J($$$"""{"name":"app-1","type":"microsoft.powerapps/canvasapps","properties":{"displayName":"Expense approvals","environmentId":"env-prod","ownerId":"{{{Priya}}}","createdAt":"2025-01-10T09:00:00Z","lastModifiedAt":"2026-10-01T09:00:00Z","isQuarantined":false,"powerPlatformConnectors":[{"connectorId":"shared_sharepointonline","operations":[]}]}}"""),
        J($$$"""{"name":"app-2","type":"microsoft.powerapps/canvasapps","properties":{"displayName":"Dropbox uploader","environmentId":"env-fin","ownerId":"{{{Gone}}}","powerPlatformConnectors":[{"connectorId":"shared_dropbox","operations":[]}]}}"""),
        J($$$"""{"name":"flow-1","type":"microsoft.powerautomate/cloudflows","properties":{"displayName":"Send files to partner","environmentId":"env-prod","ownerId":"{{{Lena}}}","trigger":"sharepointonline","powerPlatformConnectors":[{"connectorId":"shared_sftpwithssh","operations":[{"operationId":"CreateFile"}]}]}}"""),
        J($$$"""{"name":"flow-2","type":"microsoft.powerautomate/cloudflows","properties":{"displayName":"Weekly digest","environmentId":"env-prod","ownerId":"{{{Priya}}}","powerPlatformConnectors":[{"connectorId":"shared_office365","operations":[]}]}}"""),
        J($$$"""{"name":"agent-1","type":"microsoft.copilotstudio/agents","properties":{"displayName":"HR helper","environmentId":"env-prod","ownerId":"{{{Priya}}}","createdAt":"2026-09-01T08:00:00Z","lastPublishedAt":"2026-10-20T08:00:00Z","createdIn":"Copilot Studio","authentication":"None","channels":["Teams","Website"],"sharedWithViewers":{"userCount":0,"groupCount":0,"entireTenant":true},"powerPlatformConnectors":[{"connectorId":"shared_excelonlinebusiness","operations":[{"operationId":"RunScriptProd","usedAs":"Tool","isEnabled":true,"connectionProvider":"Maker"}]}]}}"""),
        J($$$$"""{"name":"agent-2","type":"microsoft.copilotstudio/agents","properties":{"displayName":"Draft bot","environmentId":"env-fin","ownerId":"{{{{Lena}}}}","authentication":"None","sharedWithViewers":{"userCount":3,"groupCount":1,"entireTenant":false}}}"""),
        J("""{"name":"conn-1","type":"microsoft.powerplatformconnector/connectors","properties":{"displayName":"SharePoint"}}"""),
    ];

    private static PowerPlatformAppDetails ProdDetails() => new()
    {
        EnvironmentId = "env-prod",
        Items =
        [
            J("""{"name":"app-1","properties":{"displayName":"Expense approvals","owner":{"displayName":"Priya Shah"},"bypassConsent":true,"sharedUsersCount":120,"sharedGroupsCount":1,"connectionReferences":[{"displayName":"Contoso ERP","isCustomApiConnection":true,"isOnPremiseConnection":true}]}}"""),
        ],
    };

    private static Dictionary<string, PowerPlatformOwner> Owners() => new()
    {
        [Priya] = new(Priya, "Priya Shah", "priya@contoso.com", true, true),
        [Lena] = new(Lena, "Lena Novak", "lena@contoso.com", false, true),
        [Gone] = new(Gone, null, null, null, false),
    };

    private static PowerPlatformInventoryResult Evaluate(PowerPlatformEvaluateRequest? request = null, IReadOnlyDictionary<string, PowerPlatformOwner>? owners = null) =>
        PowerPlatformEvaluator.Evaluate(request ?? new PowerPlatformEvaluateRequest { Resources = Resources(), AppDetails = [ProdDetails()] },
            Options(), owners ?? Owners(), Now);

    [Fact]
    public void Environments_are_counted_with_what_they_hold_and_connectors_rows_are_ignored()
    {
        var r = Evaluate();

        Assert.Equal(["Contoso (default)", "Finance"], r.Environments.Select(e => e.Name));
        var prod = r.Environments[0];
        Assert.Equal(("Default", "europe", false), (prod.Type, prod.Region, prod.IsManaged));
        Assert.Equal((1, 2, 1), (prod.Apps, prod.Flows, prod.Agents));
        Assert.Equal(2, r.Apps.Count);
    }

    [Fact]
    public void Apps_take_sharing_and_consent_from_the_admin_detail_and_connectors_from_both()
    {
        var expense = Assert.Single(Evaluate().Apps, a => a.Id == "app-1");

        Assert.Equal("Contoso (default)", expense.EnvironmentName);
        Assert.Equal("Priya Shah", expense.Owner);
        Assert.Equal((120, 1, true), (expense.SharedUsers, expense.SharedGroups, expense.BypassConsent));
        Assert.Equal(["shared_sharepointonline", "Contoso ERP"], expense.Connectors);
        Assert.Equal(
            [PowerPlatformEvaluator.ConsentBypassedFlag, PowerPlatformEvaluator.CustomConnectorFlag, PowerPlatformEvaluator.GatewayFlag,
             "Shared with 120 people", PowerPlatformEvaluator.SharedWithGroupFlag],
            expense.Flags.Select(f => f.Label));
    }

    [Fact]
    public void An_app_without_admin_detail_still_gets_connector_and_owner_flags()
    {
        var uploader = Assert.Single(Evaluate().Apps, a => a.Id == "app-2");

        Assert.Null(uploader.SharedUsers);
        Assert.Null(uploader.BypassConsent);
        Assert.Equal(["High-risk connector: shared_dropbox", PowerPlatformEvaluator.OwnerGoneFlag], uploader.Flags.Select(f => f.Label));
    }

    [Fact]
    public void Sharing_counts_the_admin_detail_leaves_out_stay_unknown_rather_than_zero()
    {
        var details = new PowerPlatformAppDetails
        {
            EnvironmentId = "env-prod",
            Items = [J("""{"name":"app-1","properties":{"displayName":"Expense approvals","bypassConsent":false}}""")],
        };
        var expense = Assert.Single(Evaluate(new PowerPlatformEvaluateRequest { Resources = Resources(), AppDetails = [details] }).Apps, a => a.Id == "app-1");

        Assert.Equal(((int?)null, (int?)null, (bool?)false), (expense.SharedUsers, expense.SharedGroups, expense.BypassConsent));
    }

    [Fact]
    public void Flows_flag_high_risk_connectors_and_a_disabled_creator()
    {
        var r = Evaluate();

        var send = Assert.Single(r.Flows, f => f.Id == "flow-1");
        Assert.Equal("sharepointonline", send.Trigger);
        Assert.Equal("Lena Novak", send.Owner);
        Assert.Equal(["High-risk connector: shared_sftpwithssh", PowerPlatformEvaluator.CreatorDisabledFlag], send.Flags.Select(f => f.Label));
        Assert.Empty(Assert.Single(r.Flows, f => f.Id == "flow-2").Flags);
    }

    [Fact]
    public void Agents_flag_no_sign_in_whole_organisation_sharing_and_tools_on_the_makers_connection()
    {
        var r = Evaluate();

        var hr = Assert.Single(r.Agents, a => a.Id == "agent-1");
        Assert.Equal(("None", true, "Copilot Studio"), (hr.Authentication, hr.SharedWithTenant, hr.CreatedIn));
        Assert.Equal(["Teams", "Website"], hr.Channels);
        Assert.NotNull(hr.Published);
        Assert.Equal(
            [PowerPlatformEvaluator.NoSignInFlag, PowerPlatformEvaluator.WholeOrganisationFlag, PowerPlatformEvaluator.MakerConnectionFlag],
            hr.Flags.Select(f => f.Label));
        Assert.Equal("high", hr.Flags[0].Severity);

        // Unpublished: no sign-in is noted, not raised; its owner is disabled.
        var draft = Assert.Single(r.Agents, a => a.Id == "agent-2");
        Assert.Null(draft.Published);
        Assert.Equal((3, 1), (draft.SharedUsers, draft.SharedGroups));
        Assert.Equal([PowerPlatformEvaluator.NoSignInDraftFlag, PowerPlatformEvaluator.OwnerDisabledFlag], draft.Flags.Select(f => f.Label));
    }

    [Fact]
    public void The_summary_counts_what_needs_a_look()
    {
        Assert.Equal(new PowerPlatformSummary(Environments: 2, Apps: 2, Flows: 2, Agents: 2, NeedALook: 5, ConsentBypassed: 1,
            HighRiskConnector: 2, AgentsWithoutSignIn: 1, SharedWithEveryone: 1, OwnerGone: 3), Evaluate().Summary);
    }

    [Fact]
    public void Without_owner_names_nothing_is_flagged_as_gone_and_ids_stay()
    {
        var r = PowerPlatformEvaluator.Evaluate(new PowerPlatformEvaluateRequest { Resources = Resources() }, Options(), owners: null, Now);

        Assert.False(r.OwnersResolved);
        string[] ownerFlags = [PowerPlatformEvaluator.OwnerGoneFlag, PowerPlatformEvaluator.OwnerDisabledFlag,
                               PowerPlatformEvaluator.CreatorGoneFlag, PowerPlatformEvaluator.CreatorDisabledFlag];
        var all = r.Apps.SelectMany(a => a.Flags).Concat(r.Flows.SelectMany(f => f.Flags)).Concat(r.Agents.SelectMany(a => a.Flags));
        Assert.DoesNotContain(all, f => ownerFlags.Contains(f.Label));
        Assert.Equal(0, r.Summary.OwnerGone);
        Assert.Equal(Gone, Assert.Single(r.Apps, a => a.Id == "app-2").OwnerId);
    }

    [Fact]
    public void Partial_reads_are_explained()
    {
        var details = ProdDetails();
        details.Complete = false;
        var r = Evaluate(new PowerPlatformEvaluateRequest
        {
            Resources = Resources(), ResourcesComplete = false,
            AppDetails = [details, new PowerPlatformAppDetails { EnvironmentId = "env-fin", Error = "access refused" }],
            AppDetailsSkipped = 4,
        });

        Assert.Contains(r.Notes, n => n.Contains("stopped before its last page"));
        Assert.Contains("Contoso (default): only part of the app detail could be read.", r.Notes);
        Assert.Contains("Finance: app sharing and consent detail couldn't be read (access refused).", r.Notes);
        Assert.Contains(r.Notes, n => n.Contains("4 more weren't"));
    }

    [Fact]
    public void Owner_ids_to_resolve_are_distinct_guids_only()
    {
        var ids = PowerPlatformEvaluator.OwnerIds(new PowerPlatformEvaluateRequest
        {
            Resources = [.. Resources(), J("""{"name":"x","type":"microsoft.powerapps/canvasapps","properties":{"ownerId":"not-a-guid"}}""")],
        });
        Assert.Equal([Priya, Gone, Lena], ids);
    }

    [Theory]
    [InlineData("HTTP with Microsoft Entra ID", true)]
    [InlineData("shared_ftp", true)]
    [InlineData("shared_sftpwithssh", true)]
    [InlineData("shared_gmail", true)]
    [InlineData("shared_sharepointonline", false)]
    [InlineData("Dropbox Business", false)] // no trailing * on Dropbox: exact match only
    public void High_risk_matching_uses_the_configured_list_on_names_and_ids(string connector, bool risky) =>
        Assert.Equal(risky, PowerPlatformEvaluator.HighRiskFlags([connector], Options()).Any());

    // ── Owner names through Graph ──────────────────────────────────────────────

    private sealed class FixedToken : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("token", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    private sealed class FakeGraph(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            lock (Requests) Requests.Add((request.Method, url));
            return Task.FromResult(respond(url));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static PowerPlatformOwners Lookup(FakeGraph graph) =>
        new(new GraphApiClient(new HttpClient(graph), Options(), new FixedToken(), new GraphMetrics()), NullLogger<PowerPlatformOwners>.Instance);

    [Fact]
    public async Task Owners_are_read_15_at_a_time_and_an_id_Graph_doesnt_return_is_gone()
    {
        var ids = Enumerable.Range(1, 20).Select(i => $"bbbbbbbb-0000-0000-0000-{i:D12}").ToList();
        var graph = new FakeGraph(url => url.Contains(ids[0])
            ? Json($$"""{"value":[{"id":"{{ids[0]}}","displayName":"Priya Shah","userPrincipalName":"priya@contoso.com","accountEnabled":true}]}""")
            : Json("""{"value":[]}"""));

        var owners = await Lookup(graph).ResolveAsync(ids, CancellationToken.None);

        Assert.NotNull(owners);
        Assert.Equal(2, graph.Requests.Count(r => r.Url.Contains("/v1.0/users?")));
        Assert.All(graph.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Contains("/v1.0/users?$filter=id in (", graph.Requests[0].Url);
        Assert.Equal(("Priya Shah", true, true), (owners![ids[0]].Name, owners[ids[0]].Enabled, owners[ids[0]].Found));
        Assert.False(owners[ids[19]].Found);
    }

    [Fact]
    public async Task A_Graph_failure_means_no_owner_names_rather_than_everyone_gone()
    {
        var graph = new FakeGraph(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") });
        Assert.Null(await Lookup(graph).ResolveAsync([Priya], CancellationToken.None));
    }

    [Fact]
    public async Task A_creator_that_is_a_service_principal_is_named_not_gone()
    {
        var graph = new FakeGraph(url => url.Contains("/v1.0/servicePrincipals?")
            ? Json($$"""{"value":[{"id":"{{Lena}}","displayName":"Flow automation","accountEnabled":true}]}""")
            : Json("""{"value":[]}"""));

        var owners = await Lookup(graph).ResolveAsync([Lena, Gone], CancellationToken.None);

        Assert.Equal(("Flow automation", true), (owners![Lena].Name, owners[Lena].Found));
        Assert.False(owners[Gone].Found);
        // Only the ids users didn't return are asked of service principals.
        Assert.Contains(graph.Requests, r => r.Url.Contains("/v1.0/servicePrincipals?") && r.Url.Contains(Gone));
    }

    [Fact]
    public async Task A_Graph_timeout_means_no_owner_names_rather_than_a_failed_evaluation()
    {
        var graph = new FakeGraph(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        Assert.Null(await Lookup(graph).ResolveAsync([Priya], CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_the_request_still_cancels()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var graph = new FakeGraph(_ => Json("""{"value":[]}"""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Lookup(graph).ResolveAsync([Priya], cts.Token));
    }

    [Fact]
    public async Task Ids_that_arent_guids_never_reach_the_filter()
    {
        var graph = new FakeGraph(_ => Json("""{"value":[]}"""));
        var owners = await Lookup(graph).ResolveAsync(["x') or startswith(displayName,'a"], CancellationToken.None);
        Assert.Empty(owners!);
        Assert.Empty(graph.Requests);
    }
}
