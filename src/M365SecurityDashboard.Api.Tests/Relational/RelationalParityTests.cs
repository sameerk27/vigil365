using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace M365SecurityDashboard.Api.Tests.Relational;

/// <summary>
/// The same behaviour, asserted on real SQL Server and real PostgreSQL. Each
/// test is a theory over <see cref="RelationalEngines.All"/>, so a regression
/// on one engine names that engine in the failure. Scope is deliberately the
/// things that differ between engines or that the in-memory provider cannot
/// check; ordinary business logic stays in the fast in-memory tests.
/// </summary>
[Collection(RelationalEnginesCollection.Name)]
public sealed class RelationalParityTests(RelationalEngines engines)
{
    private static SecurityAlert Alert(string? externalId, string type = "SignInRisk") => new()
    {
        ExternalId = externalId,
        AlertType = type,
        Service = M365ServiceArea.EntraId,
        Severity = AlertSeverity.High,
        Title = "parity",
        DetectedAt = DateTimeOffset.UtcNow,
        LastUpdatedAt = DateTimeOffset.UtcNow,
        RawJson = "{}",
    };

    // ── Schema ────────────────────────────────────────────────────────────────

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Migrations_apply_cleanly_from_an_empty_database(DatabaseProvider provider)
    {
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Equal(db.Database.GetMigrations().Count(), applied.Count());

        // Every DbSet is queryable — i.e. every table the model expects exists
        // with the columns EF will select. A missing table or column throws here.
        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var set = (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                .MakeGenericMethod(entityType.ClrType).Invoke(db, null)!;
            await set.Cast<object>().AsNoTracking().Take(1).ToListAsync();
        }
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Migrating_twice_is_a_no_op(DatabaseProvider provider)
    {
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    // ── Column types ──────────────────────────────────────────────────────────

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task DateTimeOffset_round_trips_to_the_microsecond(DatabaseProvider provider)
    {
        // Postgres timestamptz keeps microseconds; SQL Server datetimeoffset keeps
        // 100ns. Microseconds are the common precision the app can rely on.
        var instant = new DateTimeOffset(2026, 9, 3, 12, 34, 56, 789, TimeSpan.Zero).AddTicks(1230);
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);

        var alert = Alert("dto-1");
        alert.DetectedAt = instant;
        db.SecurityAlerts.Add(alert);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var back = await db.SecurityAlerts.SingleAsync(a => a.ExternalId == "dto-1");
        Assert.Equal(instant, back.DetectedAt);
        Assert.Equal(TimeSpan.Zero, back.DetectedAt.Offset);
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Unbounded_text_columns_accept_far_more_than_8000_characters(DatabaseProvider provider)
    {
        // nvarchar(max) / text. A bounded varchar would truncate or throw.
        var big = new string('x', 200_000);
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);

        var alert = Alert("big");
        alert.RawJson = big;
        db.SecurityAlerts.Add(alert);
        db.AuditEvents.Add(new AuditEvent { ExternalId = "ev-big", Activity = "x", OccurredAt = DateTimeOffset.UtcNow, RawJson = big });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(big.Length, (await db.SecurityAlerts.SingleAsync(a => a.ExternalId == "big")).RawJson.Length);
        Assert.Equal(big.Length, (await db.AuditEvents.SingleAsync(e => e.ExternalId == "ev-big")).RawJson.Length);
    }

    // ── Keys and indexes ──────────────────────────────────────────────────────

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Identity_keys_are_generated_by_the_database(DatabaseProvider provider)
    {
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);
        var a = Alert("id-a");
        var b = Alert("id-b");
        db.SecurityAlerts.AddRange(a, b);
        await db.SaveChangesAsync();

        Assert.True(a.Id > 0);
        Assert.True(b.Id > 0);
        Assert.NotEqual(a.Id, b.Id);
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Singleton_rows_with_fixed_key_1_insert_without_identity_conflict(DatabaseProvider provider)
    {
        // These tables use ValueGeneratedNever so the app can INSERT Id = 1.
        // On an identity column that INSERT fails, which crashed fresh installs
        // once already on SQL Server; Postgres identity-by-default has the same
        // trap in the other direction if the annotation is lost. (TenantBaseline
        // is keyed by tenant now and is covered by TenantIsolationTests.)
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);
        db.NotificationSettings.Add(new NotificationSettings { Id = 1 });
        db.GraphConfig.Add(new GraphConfig { Id = 1, TenantId = "t", ClientId = "c", UpdatedAt = DateTimeOffset.UtcNow });
        db.MetricsCounters.Add(new MetricsCounters { Id = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, (await db.NotificationSettings.SingleAsync()).Id);
        Assert.Equal(1, (await db.GraphConfig.SingleAsync()).Id);
        Assert.Equal(1, (await db.MetricsCounters.SingleAsync()).Id);
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Filtered_unique_index_ignores_null_ExternalId_and_rejects_duplicates(DatabaseProvider provider)
    {
        // The one index whose filter is spelled per engine ("[ExternalId]" vs
        // "\"ExternalId\""). If the spelling were wrong the migration would have
        // failed; if the filter were dropped, the two NULL inserts would collide.
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);

        db.SecurityAlerts.AddRange(Alert(null), Alert(null), Alert("dup"));
        await db.SaveChangesAsync();
        Assert.Equal(3, await db.SecurityAlerts.CountAsync());

        db.SecurityAlerts.Add(Alert("dup"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task AuditEvent_dedupe_index_rejects_the_same_source_and_external_id(DatabaseProvider provider)
    {
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);
        db.AuditEvents.Add(new AuditEvent { Source = "directoryAudit", ExternalId = "e1", Activity = "a", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.AuditEvents.Add(new AuditEvent { Source = "directoryAudit", ExternalId = "e1", Activity = "b", OccurredAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ── Services that touch the database directly ─────────────────────────────

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Retention_prune_deletes_only_expired_rows(DatabaseProvider provider)
    {
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);
        var old = Alert("old");
        old.IsResolved = true;
        old.LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-400);
        var recent = Alert("recent");
        recent.IsResolved = true;
        var open = Alert("open");
        open.LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-400);
        db.SecurityAlerts.AddRange(old, recent, open);
        await db.SaveChangesAsync();

        var summary = await DataRetentionWorker.PruneAsync(db, new RetentionOptions { ResolvedAlertsDays = 90 }, CancellationToken.None);

        Assert.Equal(1, summary.ResolvedAlerts);
        var left = await db.SecurityAlerts.AsNoTracking().Select(a => a.ExternalId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { "open", "recent" }, left);
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Database_size_query_returns_a_real_figure(DatabaseProvider provider)
    {
        // The only raw SQL the app runs. A wrong catalog name returns null via
        // the catch in MetricsService, which the Metrics tab would show as
        // "unavailable" rather than failing — so null must be a test failure.
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);
        var svc = new MetricsService(db, new MetricsState(), Options.Create(new RetentionOptions()), NullLogger<MetricsService>.Instance);

        var metrics = await svc.GatherAsync(CancellationToken.None);

        Assert.NotNull(metrics.DbSizeBytes);
        Assert.True(metrics.DbSizeBytes > 0, $"size was {metrics.DbSizeBytes}");
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Default_policy_seed_is_idempotent(DatabaseProvider provider)
    {
        await using var db = await engines.CreateMigratedDatabaseAsync(provider);
        AlertingSchema.SeedDefaultPolicies(db);
        var first = await db.AlertPolicies.CountAsync();
        Assert.True(first > 0);

        AlertingSchema.SeedDefaultPolicies(db);
        Assert.Equal(first, await db.AlertPolicies.CountAsync());
    }
}
