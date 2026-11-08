using System.Net;
using System.Text;
using System.Text.Json;
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
/// Copilot agent and Power Platform alerts from the Purview audit log: how records
/// become activity names, how the asynchronous search advances across runs, that a
/// missing optional permission never marks a client as failing, that late records
/// still alert, and that the audit search is the only Graph POST.
/// Operation strings for Power Automate and Power Apps below are samples: Microsoft
/// documents friendly names only, so classification is by fields and keywords.
/// </summary>
public sealed class PurviewAuditTests
{
    private static readonly string[] Risky = ["HTTP*", "FTP*", "SFTP*", "Gmail", "Dropbox"];
    private static readonly HashSet<string> Domains = new(StringComparer.OrdinalIgnoreCase) { "contoso.com" };

    private static NormalizedAuditEvent? N(string json) =>
        PurviewAuditNormalizer.Normalize(JsonDocument.Parse(json).RootElement, Risky, upn => PurviewAuditNormalizer.IsExternal(upn, Domains));

    // ── Normalising records ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("BotUpdateOperation-BotAuthUpdate", "Agent authentication changed")]
    [InlineData("BotUpdateOperation-BotShare", "Agent shared")]
    [InlineData("BotUpdateOperation-BotPublish", "Agent published")]
    [InlineData("AIPluginOperationCreate", "Agent action added")]
    [InlineData("BotCreate", "Agent created")]
    [InlineData("BotDelete", "Agent deleted")]
    public void Copilot_Studio_labels_become_agent_activities(string operation, string expected)
    {
        var e = N($$$"""{"operation":"{{{operation}}}","userPrincipalName":"priya@contoso.com","auditData":{"BotSchemaName":"cr123_hrHelper","BotId":"b1"}}""");

        Assert.NotNull(e);
        Assert.Equal(PurviewAuditNormalizer.CopilotAgents, e!.Product);
        Assert.Equal(expected, e.Activity);
        Assert.Equal("priya@contoso.com", e.ActorUpn);
        Assert.Equal("cr123_hrHelper", e.TargetName);
    }

    [Theory]
    [InlineData("BotDeleteCleanup")]   // follow-up of a delete already counted
    [InlineData("CopilotInteraction")] // usage, not a change
    public void Copilot_usage_and_follow_up_records_are_skipped(string operation)
        => Assert.Null(N($$$"""{"operation":"{{{operation}}}","auditData":{"BotId":"b1"}}"""));

    [Fact]
    public void Every_Copilot_Studio_label_that_is_searched_for_maps_to_an_activity()
    {
        Assert.DoesNotContain("CopilotInteraction", PurviewAuditNormalizer.CopilotStudioOperations);
        Assert.DoesNotContain("BotDeleteCleanup", PurviewAuditNormalizer.CopilotStudioOperations);
        foreach (var op in PurviewAuditNormalizer.CopilotStudioOperations)
            Assert.NotNull(N($$"""{"operation":"{{op}}"}"""));
    }

    [Theory]
    [InlineData("SharePoint, Office 365 Outlook, HTTP", "Flow created (high-risk connector: HTTP)")]
    [InlineData("""["SharePoint","HTTP with Microsoft Entra ID"]""", "Flow created (high-risk connector: HTTP with Microsoft Entra ID)")]
    [InlineData("SharePoint; Gmail | Dropbox", "Flow created (high-risk connector: Gmail, Dropbox)")]
    [InlineData("SharePoint, Office 365 Outlook", "Flow created")]
    public void A_flow_created_with_a_listed_connector_says_which(string connectors, string expected)
    {
        var data = JsonSerializer.Serialize(new { FlowConnectorNames = connectors, FlowDetailsUrl = "https://make.powerautomate.com/environments/e1/flows/f-123/details" });
        var e = N($$$"""{"auditLogRecordType":"microsoftFlow","operation":"CreateFlow","userPrincipalName":"j.smith@contoso.com","auditData":{{{data}}}}""");

        Assert.Equal(PurviewAuditNormalizer.PowerAutomate, e!.Product);
        Assert.Equal(expected, e.Activity);
        Assert.Equal("f-123", e.TargetName);
    }

    [Fact]
    public void An_edited_flow_keeps_the_high_risk_qualifier()
        => Assert.Equal("Flow edited (high-risk connector: SFTP - SSH)",
            N("""{"operation":"EditFlow","auditData":{"FlowConnectorNames":"SFTP - SSH, OneDrive"}}""")!.Activity);

    [Theory]
    [InlineData("alex_fabrikam.com#EXT#@contoso.onmicrosoft.com", "3", "Flow shared (external: alex_fabrikam.com#EXT#@contoso.onmicrosoft.com)")]
    [InlineData("alex@fabrikam.com", "2", "Flow shared (external: alex@fabrikam.com)")]
    [InlineData("ravi@contoso.com", "3", "Flow co-owner added: ravi@contoso.com")]
    [InlineData("ravi@contoso.com", "2", "Flow shared (run-only): ravi@contoso.com")]
    public void Flow_sharing_says_who_and_whether_they_are_outside_the_organisation(string recipient, string permission, string expected)
    {
        var e = N($$$"""{"auditLogRecordType":"microsoftFlow","operation":"EditFlowPermissions","auditData":{"RecipientUPN":"{{{recipient}}}","SharingPermission":{{{permission}}}}}""");
        Assert.Equal(expected, e!.Activity);
    }

    [Fact]
    public void Removing_a_share_and_an_admin_delete_are_told_apart()
    {
        Assert.Equal("Flow sharing removed: ravi@contoso.com",
            N("""{"auditLogRecordType":"microsoftFlow","operation":"DeleteFlowPermissions","auditData":{"RecipientUPN":"ravi@contoso.com"}}""")!.Activity);
        Assert.Equal("Flow deleted by admin",
            N("""{"auditLogRecordType":"microsoftFlow","operation":"DeleteFlow","auditData":{"UserTypeInitiated":2}}""")!.Activity);
        Assert.Equal("Flow deleted",
            N("""{"auditLogRecordType":"microsoftFlow","operation":"DeleteFlow","auditData":{"UserTypeInitiated":1}}""")!.Activity);
    }

    [Fact]
    public void Licence_trials_are_skipped()
        => Assert.Null(N("""{"auditLogRecordType":"microsoftFlow","operation":"StartPaidTrial","auditData":{}}"""));

    private static string AppRecord(string activityName, string? upn = null) => $$$"""
        {"operation":"PowerAppsAdminActivity"{{{(upn is null ? "" : $",\"userPrincipalName\":\"{upn}\"")}}},
         "auditData":{"PropertyCollection":[
           {"Name":"powerplatform.analytics.resource.type","Value":"PowerApp"},
           {"Name":"powerplatform.analytics.activity.name","Value":"{{{activityName}}}"},
           {"Name":"powerplatform.analytics.resource.power_app.display_name","Value":"Expense Tracker"},
           {"Name":"enduser.principal_name","Value":"admin@contoso.com"}]}}
        """;

    [Theory]
    [InlineData("AdminSetBypassConsentState", "App consent bypass changed")]
    [InlineData("AdminAllowThirdPartyApps", "Third-party apps allowed")]
    [InlineData("AdminSetQuarantineState", "App quarantine state changed")]
    [InlineData("AdminModifyAppOwner", "App owner changed")]
    [InlineData("EditAppPermission", "App permissions changed")]
    [InlineData("Edited app permission", "App permissions changed")]
    [InlineData("DeleteAppPermission", "App permission removed")]
    [InlineData("PublishApp", "App published")]
    [InlineData("AdminDeleteApp", "App deleted")]
    public void Power_Apps_activities_are_named_for_the_operator(string activityName, string expected)
    {
        var e = N(AppRecord(activityName));

        Assert.Equal(PurviewAuditNormalizer.PowerApps, e!.Product);
        Assert.Equal(expected, e.Activity);
        Assert.Equal("Expense Tracker", e.TargetName);
        Assert.Equal("admin@contoso.com", e.ActorUpn); // from the property collection when the record has no UPN
    }

    [Theory]
    [InlineData("LaunchPowerApp")]
    [InlineData("MarkAppAsFeatured")]
    public void App_launches_and_cosmetic_settings_are_skipped(string activityName) => Assert.Null(N(AppRecord(activityName)));

    [Fact]
    public void Power_Platform_admin_records_are_named_by_record_type()
    {
        Assert.Equal("Power Platform DLP policy changed (UpdatePolicy)",
            N("""{"auditLogRecordType":"powerPlatformAdminDlp","operation":"UpdatePolicy"}""")!.Activity);
        Assert.Equal("Environment created",
            N("""{"auditLogRecordType":"powerPlatformAdminEnvironment","operation":"CreateEnvironment"}""")!.Activity);
        Assert.Equal("Environment deleted",
            N("""{"auditLogRecordType":"powerPlatformAdminEnvironment","operation":"DeleteEnvironment"}""")!.Activity);
    }

    [Theory]
    [InlineData("CopyEnvironment", "Environment copied")]
    [InlineData("RestoreEnvironment", "Environment restored")]
    [InlineData("RecoverDeletedEnvironment", "Environment recovered")]
    [InlineData("HardDeleteEnvironment", "Environment deleted")]
    [InlineData("Changed property on environment", "Environment changed (Changed property on environment)")]
    public void Environment_events_are_told_apart_by_keyword(string operation, string expected)
        => Assert.Equal(expected, N($$$"""{"auditLogRecordType":"powerPlatformAdminEnvironment","operation":"{{{operation}}}"}""")!.Activity);

    [Fact]
    public void A_change_to_an_environments_security_group_is_named_for_what_it_is()
    {
        var e = N("""
            {"auditLogRecordType":"powerPlatformAdminEnvironment","operation":"Changed property on environment",
             "auditData":{"EnvironmentName":"Finance (prod)","PropertyCollection":[{"Name":"powerplatform.analytics.resource.environment.property_name","Value":"SecurityGroupId"}]}}
            """);
        Assert.Equal("Environment security group changed", e!.Activity);
        Assert.Equal("Finance (prod)", e.TargetName);
    }

    // Shape from Microsoft's PowerPlatformAdministratorActivity example: the detail is in PropertyCollection.
    private static string AdminRecord(string operation, params (string Name, string Value)[] props) => $$$"""
        {"operation":"{{{operation}}}","userPrincipalName":"admin@contoso.com",
         "auditData":{"PropertyCollection":[{{{string.Join(",", props.Select(p => $"{{\"Name\":\"{p.Name}\",\"Value\":\"{p.Value}\"}}"))}}}]}}
        """;

    private const string DlpOp = "powerplatform.analytics.resource.tenant.governance.api_policy.operation_name";
    private const string DisplayName = "powerplatform.analytics.resource.display_name";

    [Theory]
    [InlineData("DeleteDlpPolicy", "Power Platform DLP policy deleted")]
    [InlineData("CreateDlpPolicy", "Power Platform DLP policy created")]
    [InlineData("UpdateDlpPolicy", "Power Platform DLP policy changed (UpdateDlpPolicy)")]
    [InlineData("DeleteConnectorBlockingPolicy", "Power Platform connector policy changed (DeleteConnectorBlockingPolicy)")]
    [InlineData("UpdateCustomConnectorPatterns", "Power Platform connector policy changed (UpdateCustomConnectorPatterns)")]
    public void Data_policy_records_say_what_was_done_to_the_policy(string subOperation, string expected)
    {
        var e = N(AdminRecord("GovernanceApiPolicyOperation", (DlpOp, subOperation), (DisplayName, "Block consumer connectors")));

        Assert.Equal(PurviewAuditNormalizer.PowerPlatformAdmin, e!.Product);
        Assert.Equal(expected, e.Activity);
        Assert.Equal("Block consumer connectors", e.TargetName);
        Assert.Equal("admin@contoso.com", e.ActorUpn);
    }

    [Theory]
    [InlineData("UpdateRuleSetOperation", "Environment group rules changed (UpdateRuleSetOperation)")]
    [InlineData("DeleteRuleBasedPolicyAssignmentOperation", "Environment group rules changed (DeleteRuleBasedPolicyAssignmentOperation)")]
    [InlineData("EnvironmentRemovedFromEnvironmentGroup", "Environment group changed (EnvironmentRemovedFromEnvironmentGroup)")]
    [InlineData("ApplyAdminRole", "Admin took System Administrator in an environment")]
    [InlineData("LockboxRequestOperation", "Customer Lockbox request (updated)")]
    public void Admin_operations_found_by_name_become_admin_activities(string operation, string expected)
    {
        var e = N(AdminRecord(operation, (DisplayName, "Production group")));
        Assert.Equal(PurviewAuditNormalizer.PowerPlatformAdmin, e!.Product);
        Assert.Equal(expected, e.Activity);
    }

    [Theory]
    [InlineData("Provisioned environment", "Environment created")]
    [InlineData("Deleted environment", "Environment deleted")]
    [InlineData("Copied environment", "Environment copied")]
    public void A_lifecycle_record_that_lists_a_security_group_keeps_its_own_name(string operation, string expected)
        => Assert.Equal(expected, N($$$"""
            {"auditLogRecordType":"powerPlatformAdminEnvironment","operation":"{{{operation}}}",
             "auditData":{"PropertyCollection":[{"Name":"powerplatform.analytics.resource.environment.security_group_id","Value":"g1"}]}}
            """)!.Activity);

    [Fact]
    public void A_data_policy_change_that_failed_does_not_read_as_done()
    {
        var e = N(AdminRecord("GovernanceApiPolicyOperation", (DlpOp, "DeleteDlpPolicy"), (DisplayName, "Block consumer connectors"),
            ("powerplatform.analytics.resource.tenant.governance.api_policy.operation_result", "False")));
        Assert.Equal("Power Platform admin action failed (DeleteDlpPolicy)", e!.Activity);
        Assert.Equal("failure", e.Result);
    }

    [Fact]
    public void A_lockbox_request_says_its_state_when_the_record_has_one()
        => Assert.Equal("Customer Lockbox request (Approved)",
            N(AdminRecord("LockboxRequestOperation", ("powerplatform.analytics.resource.lockbox_request.status", "Approved")))!.Activity);

    [Fact]
    public void Every_admin_operation_that_is_searched_for_maps_to_an_activity()
        => Assert.All(PurviewAuditNormalizer.PowerPlatformAdminOperations, op => Assert.NotNull(N(AdminRecord(op))));

    [Fact]
    public void A_record_from_another_workload_is_not_ours()
        => Assert.Null(N("""{"auditLogRecordType":"exchangeAdmin","operation":"Set-Mailbox","auditData":{}}"""));

    [Fact]
    public void A_record_with_no_verified_domains_known_counts_only_guests_as_external()
    {
        var none = new HashSet<string>();
        Assert.True(PurviewAuditNormalizer.IsExternal("a_b.com#EXT#@contoso.onmicrosoft.com", none));
        Assert.False(PurviewAuditNormalizer.IsExternal("alex@fabrikam.com", none));
    }

    // ── The asynchronous search, across runs ────────────────────────────────────

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedToken : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("token", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    /// <summary>A scripted Graph that records every request. Unscripted URLs get an empty page.</summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string? Body)> Requests = [];
        public Func<HttpMethod, string, string?, HttpResponseMessage?> Respond { get; set; } = (_, _, _) => null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Add((request.Method, url, body));
            return Respond(request.Method, url, body) ?? Json("""{"value":[]}""");
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static readonly GraphOptions Options = new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111", ClientId = "c", ClientSecret = "s", BaseUrl = "https://graph.test",
        ExchangeQuarantinePath = "/beta/quarantine", MailFlowIssuesPath = "/v1.0/mailflow",
    };

    private static PurviewAuditCollector Purview(AppDbContext db, FakeGraph graph, Clock clock) =>
        new(db, new GraphApiClient(new HttpClient(graph), Options, new FixedToken(), new GraphMetrics()),
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<PurviewAuditCollector>.Instance, clock);

    private const string FlowRecord = """
        {"id":"r-flow","createdDateTime":"2026-10-19T08:31:07Z","auditLogRecordType":"microsoftFlow","operation":"CreateFlow",
         "userPrincipalName":"j.smith@contoso.com","auditData":{"FlowConnectorNames":"SharePoint, HTTP","ResultStatus":"Succeeded"}}
        """;
    private const string AgentRecord = """
        {"id":"r-agent","createdDateTime":"2026-10-19T08:40:00Z","operation":"BotUpdateOperation-BotAuthUpdate",
         "userPrincipalName":"priya@contoso.com","auditData":{"BotSchemaName":"cr123_hrHelper"}}
        """;

    /// <summary>Searches are created as q-pp / q-cs / q-pa by feed and finish at once; records per search as given.</summary>
    private static Func<HttpMethod, string, string?, HttpResponseMessage?> Microsoft365(string ppRecords, string csRecords, string status = "succeeded", string paRecords = "") =>
        (method, url, body) =>
        {
            if (method == HttpMethod.Post && url.EndsWith("/v1.0/security/auditLog/queries"))
                return Json(body!.Contains("GovernanceApiPolicyOperation") ? """{"id":"q-pa","status":"notStarted"}"""
                    : body.Contains("operationFilters") ? """{"id":"q-cs","status":"notStarted"}""" : """{"id":"q-pp","status":"notStarted"}""");
            if (url.Contains("/queries/q-pp/records")) return Json($$"""{"value":[{{ppRecords}}]}""");
            if (url.Contains("/queries/q-cs/records")) return Json($$"""{"value":[{{csRecords}}]}""");
            if (url.Contains("/queries/q-pa/records")) return Json($$"""{"value":[{{paRecords}}]}""");
            if (url.Contains("/queries/q-")) return Json($$"""{"id":"x","status":"{{status}}"}""");
            if (url.Contains("/v1.0/domains")) return Json("""{"value":[{"id":"contoso.com","isVerified":true}]}""");
            return null;
        };

    [Fact]
    public async Task The_first_run_submits_one_search_per_feed_and_stores_nothing_yet()
    {
        using var db = TestAppDbContextFactory.Create();
        var clock = new Clock(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        var graph = new FakeGraph { Respond = Microsoft365(FlowRecord, AgentRecord) };

        await Purview(db, graph, clock).CollectAsync(CancellationToken.None);

        var posts = graph.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Equal(3, posts.Count);
        Assert.Contains(posts, p => p.Body!.Contains("\"recordTypeFilters\"") && p.Body.Contains("microsoftFlow"));
        Assert.Contains(posts, p => p.Body!.Contains("\"operationFilters\"") && p.Body.Contains("BotUpdateOperation-BotAuthUpdate"));
        // The admin record type isn't in Graph's list, so those operations are asked for by name.
        Assert.Contains(posts, p => p.Body!.Contains("\"operationFilters\"") && p.Body.Contains("GovernanceApiPolicyOperation") && p.Body.Contains("ApplyAdminRole"));
        Assert.All(posts, p => Assert.Contains("2026-10-18T10:00:00", p.Body)); // first search looks back 24 hours
        Assert.Empty(await db.AuditEvents.ToListAsync());
        Assert.All(await db.PurviewAuditStates.ToListAsync(), s => Assert.Equal(PurviewAuditCollector.StatusWaiting, s.Status));
    }

    [Fact]
    public async Task The_next_run_stores_the_records_once_and_moves_the_watermark()
    {
        using var db = TestAppDbContextFactory.Create();
        var clock = new Clock(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        var graph = new FakeGraph { Respond = Microsoft365($"{FlowRecord},{FlowRecord}", AgentRecord) }; // a duplicate in the page
        var purview = Purview(db, graph, clock);

        await purview.CollectAsync(CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(15);
        await purview.CollectAsync(CancellationToken.None);

        var events = await db.AuditEvents.OrderBy(e => e.ExternalId).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal("Agent authentication changed", events[0].Activity);
        Assert.Equal("Flow created (high-risk connector: HTTP)", events[1].Activity);
        Assert.All(events, e => Assert.Equal(PurviewAuditNormalizer.Source, e.Source));
        Assert.Equal(PurviewAuditNormalizer.PowerAutomate, events[1].Category);
        Assert.Equal("success", events[1].Result);

        var states = await db.PurviewAuditStates.ToListAsync();
        Assert.All(states, s =>
        {
            Assert.Equal(PurviewAuditCollector.StatusOk, s.Status);
            Assert.Null(s.PendingQueryId);
            Assert.Equal(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero), s.Watermark);
        });
    }

    [Fact]
    public async Task Admin_records_found_by_operation_name_are_stored_as_Power_Platform_admin_events()
    {
        using var db = TestAppDbContextFactory.Create();
        var clock = new Clock(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        const string dlpDeleted = """
            {"id":"r-dlp","createdDateTime":"2026-10-19T09:00:00Z","operation":"GovernanceApiPolicyOperation","userPrincipalName":"admin@contoso.com",
             "auditData":{"PropertyCollection":[
               {"Name":"powerplatform.analytics.resource.tenant.governance.api_policy.operation_name","Value":"DeleteDlpPolicy"},
               {"Name":"powerplatform.analytics.resource.display_name","Value":"Block consumer connectors"}]}}
            """;
        var graph = new FakeGraph { Respond = Microsoft365("", "", paRecords: dlpDeleted) };
        var purview = Purview(db, graph, clock);

        await purview.CollectAsync(CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(15);
        await purview.CollectAsync(CancellationToken.None);

        var e = await db.AuditEvents.SingleAsync();
        Assert.Equal("Power Platform DLP policy deleted", e.Activity);
        Assert.Equal(PurviewAuditNormalizer.PowerPlatformAdmin, e.Category);
        Assert.Equal("Block consumer connectors", e.TargetName);
        var admin = await db.PurviewAuditStates.SingleAsync(s => s.Feed == PurviewAuditCollector.FeedPowerPlatformAdmin);
        Assert.Equal(PurviewAuditCollector.StatusOk, admin.Status);
    }

    [Fact]
    public async Task The_next_search_waits_for_the_interval_and_overlaps_the_last_window()
    {
        using var db = TestAppDbContextFactory.Create();
        var start = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        var clock = new Clock(start);
        var graph = new FakeGraph { Respond = Microsoft365(FlowRecord, AgentRecord) };
        var purview = Purview(db, graph, clock);

        await purview.CollectAsync(CancellationToken.None);        // submit
        clock.Now = start.AddMinutes(15);
        await purview.CollectAsync(CancellationToken.None);        // read
        clock.Now = start.AddMinutes(30);
        await purview.CollectAsync(CancellationToken.None);        // too soon: nothing new
        var feeds = PurviewAuditCollector.Feeds.Count;
        Assert.Equal(feeds, graph.Requests.Count(r => r.Method == HttpMethod.Post));

        clock.Now = start.AddMinutes(61);
        await purview.CollectAsync(CancellationToken.None);        // due again
        var latest = graph.Requests.Where(r => r.Method == HttpMethod.Post).TakeLast(feeds).ToList();
        Assert.Equal(feeds, latest.Count);
        Assert.All(latest, p => Assert.Contains("2026-10-18T10:00:00", p.Body)); // watermark 10:00 less the 24-hour overlap

        clock.Now = start.AddMinutes(76);
        await purview.CollectAsync(CancellationToken.None);        // the overlapping search returns the same records
        Assert.Equal(2, await db.AuditEvents.CountAsync());        // nothing is stored twice
    }

    [Fact]
    public async Task A_search_Microsoft_never_finishes_is_abandoned_and_submitted_again()
    {
        using var db = TestAppDbContextFactory.Create();
        var start = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        var clock = new Clock(start);
        var graph = new FakeGraph { Respond = Microsoft365("", "", status: "running") };
        var purview = Purview(db, graph, clock);

        await purview.CollectAsync(CancellationToken.None);
        clock.Now = start.AddMinutes(30);
        await purview.CollectAsync(CancellationToken.None);
        Assert.All(await db.PurviewAuditStates.ToListAsync(), s => Assert.Equal(PurviewAuditCollector.StatusWaiting, s.Status));

        clock.Now = start.AddMinutes(200); // past the 180-minute timeout
        await purview.CollectAsync(CancellationToken.None);
        Assert.All(await db.PurviewAuditStates.ToListAsync(), s =>
        {
            Assert.Equal(PurviewAuditCollector.StatusFailed, s.Status);
            Assert.Null(s.PendingQueryId);
        });

        clock.Now = start.AddMinutes(215);
        await purview.CollectAsync(CancellationToken.None);
        Assert.Equal(2 * PurviewAuditCollector.Feeds.Count, graph.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Without_the_permission_the_feeds_say_so_and_the_collection_run_is_not_failing()
    {
        using var db = TestAppDbContextFactory.Create();
        var graph = new FakeGraph
        {
            Respond = (method, url, _) => method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":{"code":"Authorization_RequestDenied"}}""") }
                : null,
        };
        var metrics = new GraphMetrics();
        var client = new GraphApiClient(new HttpClient(graph), Options, new FixedToken(), metrics);
        var credentials = new TenantGraphCredentials(db, TestTenancy.For(TestTenancy.Default), Microsoft.Extensions.Options.Options.Create(Options),
            new SecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance),
            Microsoft.Extensions.Options.Options.Create(new EditionOptions()));
        var purview = new PurviewAuditCollector(db, client, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<PurviewAuditCollector>.Instance);
        var collector = new GraphCollector(db, client, Microsoft.Extensions.Options.Options.Create(Options), credentials, metrics,
            NullLogger<GraphCollector>.Instance, purview);

        var run = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(CollectionStatus.Completed, run.Status);
        Assert.Equal(0, run.SourceFailures);
        var states = await db.PurviewAuditStates.ToListAsync();
        Assert.Equal(PurviewAuditCollector.Feeds.Count, states.Count);
        Assert.All(states, s =>
        {
            Assert.Equal(PurviewAuditCollector.StatusNeedsPermission, s.Status);
            Assert.Contains("AuditLogsQuery.Read.All", s.LastError);
        });
    }

    // ── Reads that fail or come back short ──────────────────────────────────────

    private const string OutsideShareRecord = """
        {"id":"r-share","createdDateTime":"2026-10-19T08:45:00Z","auditLogRecordType":"microsoftFlow","operation":"EditFlowPermissions",
         "userPrincipalName":"j.smith@contoso.com","auditData":{"RecipientUPN":"alex@fabrikam.com","SharingPermission":2,"FlowDisplayName":"Sync invoices"}}
        """;

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}") => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Microsoft365(...), with the verified-domain read answered by <paramref name="domains"/> (null: the normal list).</summary>
    private static Func<HttpMethod, string, string?, HttpResponseMessage?> WithDomains(
        Func<HttpMethod, string, string?, HttpResponseMessage?> inner, Func<string, HttpResponseMessage?> domains) =>
        (method, url, body) => url.Contains("/v1.0/domains") ? domains(url) ?? inner(method, url, body) : inner(method, url, body);

    private static async Task<PurviewAuditState> PowerPlatformFeed(AppDbContext db) =>
        await db.PurviewAuditStates.SingleAsync(s => s.Feed == PurviewAuditCollector.FeedPowerPlatform);

    [Fact]
    public async Task A_share_that_needs_the_domain_list_waits_for_it_instead_of_being_stored_as_internal()
    {
        using var db = TestAppDbContextFactory.Create();
        var start = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        var clock = new Clock(start);
        var domainsDown = true;
        var graph = new FakeGraph
        {
            Respond = WithDomains(Microsoft365(OutsideShareRecord, ""), _ => domainsDown ? Status(HttpStatusCode.ServiceUnavailable) : null),
        };
        var purview = Purview(db, graph, clock);

        await purview.CollectAsync(CancellationToken.None);   // submit
        clock.Now = start.AddMinutes(15);
        await purview.CollectAsync(CancellationToken.None);   // results ready, domains unreadable

        var waiting = await PowerPlatformFeed(db);
        Assert.Equal(PurviewAuditCollector.StatusFailed, waiting.Status);
        Assert.StartsWith(PurviewAuditCollector.DomainsUnreadableMessage, waiting.LastError);
        Assert.NotNull(waiting.PendingQueryId);              // the same search is read again, not lost
        Assert.Null(waiting.Watermark);
        Assert.Empty(await db.AuditEvents.ToListAsync());

        domainsDown = false;
        clock.Now = start.AddMinutes(30);
        await purview.CollectAsync(CancellationToken.None);

        Assert.Equal("Flow shared (external: alex@fabrikam.com)", (await db.AuditEvents.SingleAsync()).Activity);
        Assert.Equal(PurviewAuditCollector.StatusOk, (await PowerPlatformFeed(db)).Status);
    }

    [Fact]
    public async Task Records_that_dont_depend_on_the_domain_list_are_stored_while_it_is_unreadable()
    {
        using var db = TestAppDbContextFactory.Create();
        var clock = new Clock(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        var guestShare = OutsideShareRecord.Replace("alex@fabrikam.com", "alex_fabrikam.com#EXT#@contoso.onmicrosoft.com");
        var graph = new FakeGraph
        {
            Respond = WithDomains(Microsoft365($"{FlowRecord},{guestShare}", AgentRecord),
                _ => Status(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges."}}""")),
        };
        var purview = Purview(db, graph, clock);

        await purview.CollectAsync(CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(15);
        await purview.CollectAsync(CancellationToken.None);

        var activities = await db.AuditEvents.Select(e => e.Activity).ToListAsync();
        Assert.Contains("Flow created (high-risk connector: HTTP)", activities);
        Assert.Contains("Flow shared (external: alex_fabrikam.com#EXT#@contoso.onmicrosoft.com)", activities); // a guest is external either way
        Assert.Contains("Agent authentication changed", activities);
        Assert.All(await db.PurviewAuditStates.ToListAsync(), s => Assert.Equal(PurviewAuditCollector.StatusOk, s.Status));
    }

    [Fact]
    public async Task A_domain_list_missing_a_page_is_not_taken_as_the_whole_list()
    {
        using var db = TestAppDbContextFactory.Create();
        var clock = new Clock(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        // fabrikam.com may be verified on the page that failed: that share can't be judged yet.
        var graph = new FakeGraph
        {
            Respond = WithDomains(Microsoft365(OutsideShareRecord, ""), url => url.Contains("skiptoken")
                ? Status(HttpStatusCode.InternalServerError)
                : Json("""{"value":[{"id":"contoso.com","isVerified":true}],"@odata.nextLink":"https://graph.test/v1.0/domains?$skiptoken=2"}""")),
        };
        var purview = Purview(db, graph, clock);

        await purview.CollectAsync(CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(15);
        await purview.CollectAsync(CancellationToken.None);

        var feed = await PowerPlatformFeed(db);
        Assert.Equal(PurviewAuditCollector.StatusFailed, feed.Status);
        Assert.Contains("Only part of the list came back.", feed.LastError);
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task A_results_page_that_fails_keeps_the_search_and_reads_it_again_next_run()
    {
        using var db = TestAppDbContextFactory.Create();
        var start = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        var clock = new Clock(start);
        var secondPageDown = true;
        var normal = Microsoft365(FlowRecord, AgentRecord);
        var graph = new FakeGraph
        {
            Respond = (method, url, body) =>
                url.Contains("/queries/q-pp/records") && url.Contains("skiptoken")
                    ? secondPageDown ? Status(HttpStatusCode.InternalServerError) : Json("""{"value":[]}""")
                : url.Contains("/queries/q-pp/records")
                    ? Json($$"""{"value":[{{FlowRecord}}],"@odata.nextLink":"https://graph.test/v1.0/security/auditLog/queries/q-pp/records?$skiptoken=2"}""")
                : normal(method, url, body),
        };
        var purview = Purview(db, graph, clock);

        await purview.CollectAsync(CancellationToken.None);
        clock.Now = start.AddMinutes(15);
        await purview.CollectAsync(CancellationToken.None);

        var feed = await PowerPlatformFeed(db);
        Assert.Equal(PurviewAuditCollector.StatusFailed, feed.Status);
        Assert.Equal("The audit search results could not be read in full. Trying again next run.", feed.LastError);
        Assert.Equal("q-pp", feed.PendingQueryId);
        Assert.DoesNotContain(await db.AuditEvents.ToListAsync(), e => e.ExternalId == "r-flow");

        secondPageDown = false;
        clock.Now = start.AddMinutes(30);
        await purview.CollectAsync(CancellationToken.None);
        Assert.Contains(await db.AuditEvents.ToListAsync(), e => e.ExternalId == "r-flow");
        Assert.Equal(PurviewAuditCollector.StatusOk, (await PowerPlatformFeed(db)).Status);
    }

    [Fact]
    public async Task Throttling_on_submit_is_reported_and_the_search_is_submitted_again_next_run()
    {
        using var db = TestAppDbContextFactory.Create();
        var start = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        var clock = new Clock(start);
        var throttled = true;
        var normal = Microsoft365(FlowRecord, AgentRecord);
        var graph = new FakeGraph
        {
            Respond = (method, url, body) => throttled && method == HttpMethod.Post ? Status(HttpStatusCode.TooManyRequests) : normal(method, url, body),
        };
        var purview = Purview(db, graph, clock);

        await purview.CollectAsync(CancellationToken.None);
        var feed = await PowerPlatformFeed(db);
        Assert.Equal(PurviewAuditCollector.StatusFailed, feed.Status);
        Assert.Contains("throttled this request (429)", feed.LastError);
        Assert.Null(feed.PendingQueryId);

        throttled = false;
        clock.Now = start.AddMinutes(15);
        await purview.CollectAsync(CancellationToken.None);
        Assert.Equal(PurviewAuditCollector.StatusWaiting, (await PowerPlatformFeed(db)).Status);
        Assert.Equal(2 * PurviewAuditCollector.Feeds.Count, graph.Requests.Count(r => r.Method == HttpMethod.Post)); // each feed refused once, then accepted
    }

    [Fact]
    public async Task A_refusal_carries_Microsofts_own_reason_alongside_the_permission_to_check()
    {
        using var db = TestAppDbContextFactory.Create();
        var clock = new Clock(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        // An illustrative message: Microsoft's exact wording for a tenant without audit search hasn't been captured live.
        var graph = new FakeGraph
        {
            Respond = (method, _, _) => method == HttpMethod.Post
                ? Status(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"Auditing is not enabled for this tenant."}}""")
                : null,
        };

        await Purview(db, graph, clock).CollectAsync(CancellationToken.None);

        var feed = await PowerPlatformFeed(db);
        Assert.Equal(PurviewAuditCollector.StatusNeedsPermission, feed.Status);
        Assert.StartsWith(PurviewAuditCollector.NeedsPermissionMessage, feed.LastError);
        Assert.EndsWith("Microsoft said: Auditing is not enabled for this tenant.", feed.LastError);
    }

    // ── Alerting on late records ────────────────────────────────────────────────

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static AlertEvaluator Evaluator(AppDbContext db) => new(db,
        new NotificationSender(new NullHttpClientFactory(),
            new SecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance),
            NullLogger<NotificationSender>.Instance),
        Microsoft.Extensions.Options.Options.Create(new AlertingOptions { AutoResolveDebounceCycles = 2 }),
        new MetricsState(), NullLogger<AlertEvaluator>.Instance);

    private static AlertPolicy Rule(string pattern) => new()
    {
        Id = Guid.NewGuid(), Name = pattern, Enabled = true, Kind = "activity", Category = AlertingSchema.PowerPlatformCategory,
        ActivityPattern = pattern, WindowMinutes = 60, Threshold = 1, Severity = "high",
        Condition = $"Activity \"{pattern}\" ≥ 1 in 60m", SuppressionMinutes = 60, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
    };

    [Fact]
    public async Task A_Purview_record_collected_hours_after_the_action_still_alerts()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(Rule("Flow * (high-risk connector:*"));
        db.AuditEvents.Add(new AuditEvent
        {
            ExternalId = "r1", Source = PurviewAuditNormalizer.Source, Category = PurviewAuditNormalizer.PowerAutomate,
            Activity = "Flow created (high-risk connector: HTTP)", ActorUpn = "j.smith@contoso.com", TargetName = "Sync invoices",
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-3), CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await Evaluator(db).EvaluateAsync(CancellationToken.None));
        Assert.Contains("Sync invoices", (await db.TriggeredAlerts.SingleAsync()).AffectedEntities);
    }

    [Fact]
    public async Task A_directory_audit_event_still_counts_by_when_it_happened()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(Rule("Add member to role"));
        db.AuditEvents.Add(new AuditEvent
        {
            ExternalId = "d1", Source = "directoryAudit", Activity = "Add member to role",
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-3), CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await Evaluator(db).EvaluateAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Agent authentication changed*", "Agent authentication changed")]
    [InlineData("Agent action *", "Agent action added")]
    [InlineData("Flow * (high-risk connector:*", "Flow edited (high-risk connector: HTTP)")]
    [InlineData("Flow shared (external:*", "Flow shared (external: alex@fabrikam.com)")]
    [InlineData("Flow co-owner added*", "Flow co-owner added: ravi@contoso.com")]
    [InlineData("App consent bypass*", "App consent bypass changed")]
    [InlineData("Power Platform DLP policy changed*", "Power Platform DLP policy changed (UpdatePolicy)")]
    [InlineData("Environment created*", "Environment created")]
    [InlineData("Power Platform DLP policy deleted*", "Power Platform DLP policy deleted")]
    [InlineData("Power Platform connector policy changed*", "Power Platform connector policy changed (DeleteConnectorBlockingPolicy)")]
    [InlineData("Environment group rules changed*", "Environment group rules changed (UpdateRuleSetOperation)")]
    [InlineData("Environment security group changed*", "Environment security group changed")]
    [InlineData("Admin took System Administrator*", "Admin took System Administrator in an environment")]
    [InlineData("Environment copied*", "Environment copied")]
    [InlineData("Customer Lockbox request*", "Customer Lockbox request (Approved)")]
    [InlineData("App conditional access changed*", "App conditional access changed")]
    public async Task Each_built_in_rule_matches_the_activity_the_normaliser_writes(string pattern, string activity)
    {
        Assert.True(AlertingSchema.CopilotPowerPlatformDefaults.Any(d => d.Pattern == pattern)
            || AlertingSchema.PowerPlatformAdditions.Any(d => d.Pattern == pattern));
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(Rule(pattern));
        db.AuditEvents.Add(new AuditEvent
        {
            ExternalId = "x", Source = PurviewAuditNormalizer.Source, Activity = activity,
            OccurredAt = DateTimeOffset.UtcNow, CollectedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await Evaluator(db).EvaluateAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    public async Task Bulk_deletion_alerts_at_five_flows_in_one_collection(int deleted, int expectedAlerts)
    {
        using var db = TestAppDbContextFactory.Create();
        var rule = Rule("Flow deleted*");
        rule.Threshold = AlertingSchema.PowerPlatformAdditions.Single(a => a.Name == "Many flows deleted").Threshold;
        db.AlertPolicies.Add(rule);
        for (var i = 0; i < deleted; i++)
            db.AuditEvents.Add(new AuditEvent
            {
                ExternalId = $"d{i}", Source = PurviewAuditNormalizer.Source, Activity = i % 2 == 0 ? "Flow deleted" : "Flow deleted by admin",
                OccurredAt = DateTimeOffset.UtcNow.AddHours(-2), CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
        await db.SaveChangesAsync();

        Assert.Equal(expectedAlerts, await Evaluator(db).EvaluateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_days_deletions_arriving_in_one_collection_are_not_a_burst()
    {
        using var db = TestAppDbContextFactory.Create();
        var rule = Rule("Flow deleted*");
        rule.Threshold = 5;
        db.AlertPolicies.Add(rule);
        for (var i = 0; i < 6; i++)
            db.AuditEvents.Add(new AuditEvent
            {
                ExternalId = $"d{i}", Source = PurviewAuditNormalizer.Source, Activity = "Flow deleted",
                OccurredAt = DateTimeOffset.UtcNow.AddHours(-3 * (i + 1)), CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
        await db.SaveChangesAsync();

        Assert.Equal(0, await Evaluator(db).EvaluateAsync(CancellationToken.None));
    }

    // ── Seeding the rules ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_pack_is_seeded_once_with_the_noisy_rules_off_and_a_deleted_rule_stays_deleted()
    {
        using var db = TestAppDbContextFactory.Create();
        AlertingSchema.SeedDefaultPolicies(db);
        await db.SaveChangesAsync();

        var pack = await db.AlertPolicies
            .Where(p => p.Category == AlertingSchema.CopilotCategory || p.Category == AlertingSchema.PowerPlatformCategory).ToListAsync();
        Assert.Equal(BothPacks, pack.Count);
        Assert.False(pack.Single(p => p.Name == "Agent created").Enabled);
        Assert.False(pack.Single(p => p.Name == "App published").Enabled);
        Assert.False(pack.Single(p => p.Name == "Power Platform DLP policy created").Enabled);
        Assert.True(pack.Single(p => p.Name == "Flow uses a high-risk connector").Enabled);
        var bulk = pack.Single(p => p.Name == "Many flows deleted");
        Assert.Equal(5, bulk.Threshold);
        Assert.Equal("Activity \"Flow deleted*\" ≥ 5 in 60m", bulk.Condition);

        db.AlertPolicies.Remove(pack.Single(p => p.Name == "Agent shared"));
        db.AlertPolicies.Remove(pack.Single(p => p.Name == "Many apps deleted"));
        await db.SaveChangesAsync();
        AlertingSchema.SeedDefaultPolicies(db);
        await db.SaveChangesAsync();

        Assert.DoesNotContain(await db.AlertPolicies.ToListAsync(), p => p.Name is "Agent shared" or "Many apps deleted");
        Assert.Equal(BothPacks - 2,
            await db.AlertPolicies.CountAsync(p => p.Category == AlertingSchema.CopilotCategory || p.Category == AlertingSchema.PowerPlatformCategory));
    }

    private static int BothPacks => AlertingSchema.CopilotPowerPlatformDefaults.Length + AlertingSchema.PowerPlatformAdditions.Length;

    [Fact]
    public async Task An_install_with_the_first_pack_receives_the_additions_once()
    {
        using var db = TestAppDbContextFactory.Create();
        AlertingSchema.SeedDefaultPolicies(db);
        await db.SaveChangesAsync();
        // An install from before the additions: the first pack only.
        var names = AlertingSchema.PowerPlatformAdditions.Select(a => a.Name).ToList();
        db.AlertPolicies.RemoveRange(await db.AlertPolicies.Where(p => names.Contains(p.Name)).ToListAsync());
        await db.SaveChangesAsync();

        AlertingSchema.SeedDefaultPolicies(db);
        await db.SaveChangesAsync();
        AlertingSchema.SeedDefaultPolicies(db);
        await db.SaveChangesAsync();

        Assert.Equal(AlertingSchema.PowerPlatformAdditions.Length, await db.AlertPolicies.CountAsync(p => names.Contains(p.Name)));
    }

    [Fact]
    public async Task An_existing_install_with_the_identity_pack_still_receives_the_new_pack()
    {
        using var db = TestAppDbContextFactory.Create();
        db.AlertPolicies.Add(new AlertPolicy
        {
            Id = Guid.NewGuid(), Name = "Privileged role assignment", Enabled = true, Kind = "activity", Category = "identity",
            ActivityPattern = "Add member to role", Metric = "", Severity = "critical", Condition = "x", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        AlertingSchema.SeedDefaultPolicies(db);
        await db.SaveChangesAsync();

        Assert.Equal(BothPacks,
            await db.AlertPolicies.CountAsync(p => p.Category == AlertingSchema.CopilotCategory || p.Category == AlertingSchema.PowerPlatformCategory));
    }

    // ── The audit search is the only Graph POST ─────────────────────────────────

    [Fact]
    public async Task The_Graph_client_refuses_a_POST_to_anything_but_the_audit_search()
    {
        var graph = new FakeGraph();
        var client = new GraphApiClient(new HttpClient(graph), Options, new FixedToken(), new GraphMetrics());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("/v1.0/users", new { }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("/v1.0/security/auditLog/queries/x/cancel", new { }, CancellationToken.None));
        Assert.Empty(graph.Requests);
    }

    [Fact]
    public void No_code_but_the_Graph_client_builds_a_POST_and_it_only_posts_the_audit_search()
    {
        var api = ApiDir();
        var posting = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("HttpMethod.Post"))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["GraphApiClient.cs"], posting);

        var callers = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f))
            .Where(l => l.Contains("graph.PostAsync(")).ToList();
        Assert.NotEmpty(callers);
        Assert.All(callers, l => Assert.Contains("GraphApiClient.AuditLogQueriesPath", l));
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
