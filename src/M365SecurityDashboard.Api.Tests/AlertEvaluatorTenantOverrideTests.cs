using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Per-client adjustments to an MSP-wide default policy (gate: "default policy
/// off for client 1 still fires for client 2"). Two clients share one database
/// and one default policy row; each evaluates in its own tenant context. An
/// override must change only that client's evaluation, and must never be
/// written into the shared row every other client inherits.
/// </summary>
public sealed class AlertEvaluatorTenantOverrideTests
{
    private static readonly Guid Contoso = TestTenancy.TenantA, Fabrikam = TestTenancy.TenantB;

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private AppDbContext Open(Guid tenant) => new(_options, TestTenancy.For(tenant));

    private static AlertEvaluator Evaluator(AppDbContext db) => new(db,
        new NotificationSender(new NullHttpClientFactory(),
            new SecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), NullLogger<SecretProtector>.Instance),
            NullLogger<NotificationSender>.Instance),
        Microsoft.Extensions.Options.Options.Create(new AlertingOptions { AutoResolveDebounceCycles = 2 }),
        new MetricsState(), NullLogger<AlertEvaluator>.Instance);

    /// <summary>The MSP-wide default (TenantId null): fires at <paramref name="threshold"/> risky users.</summary>
    private async Task<AlertPolicy> SharedPolicyAsync(int threshold)
    {
        var policy = new AlertPolicy
        {
            Id = Guid.NewGuid(), Name = "Risky Users", Enabled = true, Category = "identity", Metric = "riskyUsersCount",
            Threshold = threshold, Severity = "high", Condition = $"Risky users >= {threshold}", SuppressionMinutes = 60,
        };
        await using var db = Open(Contoso);
        db.AlertPolicies.Add(policy);
        await db.SaveChangesAsync();
        Assert.Null(policy.TenantId);
        return policy;
    }

    private async Task SeedRiskyUsersAsync(Guid tenant, int count)
    {
        await using var db = Open(tenant);
        for (var i = 0; i < count; i++)
            db.SecurityAlerts.Add(new SecurityAlert
            {
                AlertType = "RiskyUser", Severity = AlertSeverity.High, Service = M365ServiceArea.EntraId,
                Title = $"risky-{tenant:N}-{i}", DetectedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
    }

    private async Task OverrideAsync(Guid tenant, Guid policyId, bool? enabled = null, int? threshold = null, string? notifyEmail = null)
    {
        await using var db = Open(tenant);
        db.AlertPolicyTenantOverrides.Add(new AlertPolicyTenantOverride
        {
            PolicyId = policyId, Enabled = enabled, Threshold = threshold, NotifyEmail = notifyEmail, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(int Fired, List<TriggeredAlert> Alerts)> EvaluateAsync(Guid tenant)
    {
        await using var db = Open(tenant);
        var fired = await Evaluator(db).EvaluateAsync(CancellationToken.None);
        return (fired, await db.TriggeredAlerts.AsNoTracking().ToListAsync());
    }

    private async Task<AlertPolicy> SharedRowAsync(Guid id)
    {
        await using var db = Open(Fabrikam);
        return await db.AlertPolicies.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task A_default_policy_switched_off_for_one_client_still_fires_for_another()
    {
        var policy = await SharedPolicyAsync(threshold: 1);
        await SeedRiskyUsersAsync(Contoso, 2);
        await SeedRiskyUsersAsync(Fabrikam, 2);
        await OverrideAsync(Contoso, policy.Id, enabled: false);

        var contoso = await EvaluateAsync(Contoso);
        var fabrikam = await EvaluateAsync(Fabrikam);

        Assert.Equal(0, contoso.Fired);
        Assert.Empty(contoso.Alerts);
        Assert.Equal(1, fabrikam.Fired);
        Assert.Equal(policy.Id, Assert.Single(fabrikam.Alerts).PolicyId);
        Assert.True((await SharedRowAsync(policy.Id)).Enabled); // still on for everyone else
    }

    [Fact]
    public async Task A_threshold_override_applies_to_that_client_only_and_never_reaches_the_shared_default()
    {
        var policy = await SharedPolicyAsync(threshold: 3);
        await SeedRiskyUsersAsync(Contoso, 2);
        await SeedRiskyUsersAsync(Fabrikam, 2);
        await OverrideAsync(Contoso, policy.Id, threshold: 1, notifyEmail: "soc@contoso.test");

        var contoso = await EvaluateAsync(Contoso);   // 2 >= 1: fires at the client's threshold
        var fabrikam = await EvaluateAsync(Fabrikam); // 2 <  3: the default still applies here

        Assert.Equal(1, contoso.Fired);
        Assert.Equal(1, Assert.Single(contoso.Alerts).Threshold);
        Assert.Equal(0, fabrikam.Fired);
        Assert.Empty(fabrikam.Alerts);

        // The evaluator saved its cycle (trigger statistics) through the tracked
        // shared row: the override must not have ridden along with it.
        var shared = await SharedRowAsync(policy.Id);
        Assert.Equal(3, shared.Threshold);
        Assert.Null(shared.NotifyEmail);
        Assert.Equal(1, shared.TriggerCount);
    }
}
