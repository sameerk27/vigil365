using System.Net;
using System.Net.Sockets;
using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Who a newly raised alert is sent to, and that it is raised and sent once
/// when two evaluations of the same client overlap.
/// </summary>
public sealed class AlertEvaluatorDispatchTests
{
    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private AppDbContext Open(Guid tenant) => new(_options, TestTenancy.For(tenant));

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A webhook receiver that takes a while to answer, as a real one can (the sender allows 15s).</summary>
    private sealed class SlowHook : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(300, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>An SMTP port that accepts and hangs up: every send fails at once, and the log still names the recipient.</summary>
    private sealed class HangUpSmtp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port { get; }

        public HangUpSmtp()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try { while (true) (await _listener.AcceptTcpClientAsync()).Dispose(); }
                catch (Exception) { /* listener stopped */ }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    private static AlertEvaluator Evaluator(AppDbContext db, HttpMessageHandler? http = null) => new(db,
        new NotificationSender(new Factory(http ?? new SlowHook()),
            new SecretProtector(new EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance),
            NullLogger<NotificationSender>.Instance),
        Microsoft.Extensions.Options.Options.Create(new AlertingOptions { AutoResolveDebounceCycles = 2 }),
        new MetricsState(), NullLogger<AlertEvaluator>.Instance);

    private static AlertPolicy RiskyUsers(string? notifyEmail = null) => new()
    {
        Id = Guid.NewGuid(), Name = "Risky Users", Enabled = true, Category = "identity", Metric = "riskyUsersCount",
        Threshold = 1, Severity = "high", Condition = "Risky users >= 1", SuppressionMinutes = 60, NotifyEmail = notifyEmail,
    };

    private static void SeedRiskyUsers(AppDbContext db, int count)
    {
        for (var i = 0; i < count; i++)
            db.SecurityAlerts.Add(new SecurityAlert
            {
                AlertType = "RiskyUser", Severity = AlertSeverity.High, Service = M365ServiceArea.EntraId,
                Title = $"risky-{i}", DetectedAt = DateTimeOffset.UtcNow,
            });
    }

    private static NotificationSettings Smtp(int port) => new()
    {
        EmailEnabled = true, SmtpHost = "127.0.0.1", SmtpPort = port, SmtpUseSsl = false,
        FromAddress = "alerts@msp.test", DefaultRecipient = "soc@msp.test", MinSeverity = "low",
    };

    [Fact]
    public async Task A_policys_notify_email_receives_its_alert_in_place_of_the_default_recipient()
    {
        using var smtp = new HangUpSmtp();
        await using var db = Open(TestTenancy.Default);
        db.AlertPolicies.Add(RiskyUsers(notifyEmail: "owner@contoso.test"));
        db.NotificationSettings.Add(Smtp(smtp.Port));
        SeedRiskyUsers(db, 2);
        await db.SaveChangesAsync();

        await Evaluator(db).EvaluateAsync(CancellationToken.None);

        var email = Assert.Single(await db.NotificationLogs.Where(l => l.Channel == "email").ToListAsync());
        Assert.Equal("owner@contoso.test", email.Target);
    }

    [Fact]
    public async Task A_clients_override_address_stands_in_for_the_msp_recipient_and_the_client_still_gets_its_copy()
    {
        using var smtp = new HangUpSmtp();
        var contoso = TestTenancy.TenantA;
        var policy = RiskyUsers(); // MSP-wide default, no address of its own
        await using (var seed = Open(contoso))
        {
            seed.AlertPolicies.Add(policy);
            seed.NotificationSettings.Add(Smtp(smtp.Port));
            seed.AlertPolicyTenantOverrides.Add(new AlertPolicyTenantOverride { PolicyId = policy.Id, NotifyEmail = "it-lead@contoso.test", UpdatedAt = DateTimeOffset.UtcNow });
            seed.TenantNotificationRoutings.Add(new TenantNotificationRouting { NotifyMsp = true, NotifyClient = true, RecipientEmail = "helpdesk@contoso.test" });
            SeedRiskyUsers(seed, 2);
            await seed.SaveChangesAsync();
        }

        await using var db = Open(contoso);
        await Evaluator(db).EvaluateAsync(CancellationToken.None);

        var email = Assert.Single(await db.NotificationLogs.Where(l => l.Channel == "email").ToListAsync());
        Assert.Equal("it-lead@contoso.test,helpdesk@contoso.test", email.Target);
    }

    [Fact]
    public async Task Overlapping_evaluations_of_one_client_raise_and_notify_a_new_breach_once()
    {
        // A dashboard load and the worker evaluate the same client at once. The
        // first is still waiting on the webhook when the second looks for an
        // open alert: without a per-client gate both raise one and both notify.
        await using (var seed = Open(TestTenancy.Default))
        {
            seed.AlertPolicies.Add(RiskyUsers());
            seed.NotificationSettings.Add(new NotificationSettings { WebhookEnabled = true, WebhookUrl = "https://hook.test/in", MinSeverity = "low" });
            seed.MetricsCounters.Add(new MetricsCounters { Id = 1 });
            SeedRiskyUsers(seed, 2);
            await seed.SaveChangesAsync();
        }
        var hook = new SlowHook();
        await using var first = Open(TestTenancy.Default);
        await using var second = Open(TestTenancy.Default);

        await Task.WhenAll(
            Evaluator(first, hook).EvaluateAsync(CancellationToken.None),
            Evaluator(second, hook).EvaluateAsync(CancellationToken.None));

        await using var check = Open(TestTenancy.Default);
        Assert.Single(await check.TriggeredAlerts.ToListAsync());
        Assert.Equal(1, hook.Calls);
    }
}
