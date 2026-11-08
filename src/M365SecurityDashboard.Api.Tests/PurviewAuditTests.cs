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

    /// <summary>Searches are created as q-pp / q-cs by feed and finish at once; records per search as given.</summary>
    private static Func<HttpMethod, string, string?, HttpResponseMessage?> Microsoft365(string ppRecords, string csRecords, string status = "succeeded") =>
        (method, url, body) =>
        {
            if (method == HttpMethod.Post && url.EndsWith("/v1.0/security/auditLog/queries"))
                return Json(body!.Contains("operationFilters") ? """{"id":"q-cs","status":"notStarted"}""" : """{"id":"q-pp","status":"notStarted"}""");
            if (url.Contains("/queries/q-pp/records")) return Json($$"""{"value":[{{ppRecords}}]}""");
            if (url.Contains("/queries/q-cs/records")) return Json($$"""{"value":[{{csRecords}}]}""");
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
        Assert.Equal(2, posts.Count);
        Assert.Contains(posts, p => p.Body!.Contains("\"recordTypeFilters\"") && p.Body.Contains("microsoftFlow"));
        Assert.Contains(posts, p => p.Body!.Contains("\"operationFilters\"") && p.Body.Contains("BotUpdateOperation-BotAuthUpdate"));
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
        Assert.Equal(2, graph.Requests.Count(r => r.Method == HttpMethod.Post));

        clock.Now = start.AddMinutes(61);
        await purview.CollectAsync(CancellationToken.None);        // due again
        var latest = graph.Requests.Where(r => r.Method == HttpMethod.Post).TakeLast(2).ToList();
        Assert.Equal(2, latest.Count);
        Assert.All(latest, p => Assert.Contains("2026-10-19T08:00:00", p.Body)); // watermark 10:00 less the 120-minute overlap

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
        Assert.Equal(4, graph.Requests.Count(r => r.Method == HttpMethod.Post));
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
        Assert.Equal(2, states.Count);
        Assert.All(states, s =>
        {
            Assert.Equal(PurviewAuditCollector.StatusNeedsPermission, s.Status);
            Assert.Contains("AuditLogsQuery.Read.All", s.LastError);
        });
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
    public async Task Each_built_in_rule_matches_the_activity_the_normaliser_writes(string pattern, string activity)
    {
        Assert.Contains(AlertingSchema.CopilotPowerPlatformDefaults, d => d.Pattern == pattern);
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

    // ── Seeding the rules ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_pack_is_seeded_once_with_the_noisy_rules_off_and_a_deleted_rule_stays_deleted()
    {
        using var db = TestAppDbContextFactory.Create();
        AlertingSchema.SeedDefaultPolicies(db);
        await db.SaveChangesAsync();

        var pack = await db.AlertPolicies
            .Where(p => p.Category == AlertingSchema.CopilotCategory || p.Category == AlertingSchema.PowerPlatformCategory).ToListAsync();
        Assert.Equal(AlertingSchema.CopilotPowerPlatformDefaults.Length, pack.Count);
        Assert.False(pack.Single(p => p.Name == "Agent created").Enabled);
        Assert.False(pack.Single(p => p.Name == "App published").Enabled);
        Assert.True(pack.Single(p => p.Name == "Flow uses a high-risk connector").Enabled);

        db.AlertPolicies.Remove(pack.Single(p => p.Name == "Agent shared"));
        await db.SaveChangesAsync();
        AlertingSchema.SeedDefaultPolicies(db);
        await db.SaveChangesAsync();

        Assert.DoesNotContain(await db.AlertPolicies.ToListAsync(), p => p.Name == "Agent shared");
        Assert.Equal(AlertingSchema.CopilotPowerPlatformDefaults.Length - 1,
            await db.AlertPolicies.CountAsync(p => p.Category == AlertingSchema.CopilotCategory || p.Category == AlertingSchema.PowerPlatformCategory));
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

        Assert.Equal(AlertingSchema.CopilotPowerPlatformDefaults.Length,
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
