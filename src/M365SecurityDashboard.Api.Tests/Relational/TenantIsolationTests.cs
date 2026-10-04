using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace M365SecurityDashboard.Api.Tests.Relational;

/// <summary>
/// The isolation guarantees, proven on real SQL Server and real PostgreSQL.
/// Cross-tenant leakage is the one unrecoverable failure of the MSP edition,
/// so these run against generated SQL, not the in-memory provider (which has
/// no SQL and cannot exercise a global query filter meaningfully).
///
/// Shape: one migrated database per test; three tenants seeded; a context per
/// tenant (its own TenantContext) plus a context with no tenant. Each test
/// states one rule from MSP_EDITION_PLAN.md §2.
/// </summary>
[Collection(RelationalEnginesCollection.Name)]
public sealed class TenantIsolationTests(RelationalEngines engines)
{
    private static readonly Guid A = TestTenancy.TenantA, B = TestTenancy.TenantB, C = TestTenancy.TenantC;

    /// <summary>A migrated database with tenants A, B, C and a context factory over it.</summary>
    private async Task<Db> SetupAsync(DatabaseProvider provider)
    {
        var root = await engines.CreateMigratedDatabaseAsync(provider);
        var name = root.Database.GetDbConnection().Database;
        var cs = root.Database.GetConnectionString()!;
        await root.DisposeAsync();

        var db = new Db(provider, cs);
        await using (var admin = db.Open(null))
        {
            admin.ClientTenants.AddRange(
                new ClientTenant { Id = A, Name = "A", CreatedAt = DateTimeOffset.UtcNow },
                new ClientTenant { Id = B, Name = "B", CreatedAt = DateTimeOffset.UtcNow },
                new ClientTenant { Id = C, Name = "C", CreatedAt = DateTimeOffset.UtcNow });
            await admin.SaveChangesAsync();
        }
        return db;
    }

    private sealed class Db(DatabaseProvider provider, string connectionString)
    {
        public AppDbContext Open(Guid? tenant)
        {
            var ctx = tenant is Guid t ? TestTenancy.For(t) : TestTenancy.None();
            if (provider == DatabaseProvider.Postgres)
            {
                var o = new DbContextOptionsBuilder<PostgresAppDbContext>();
                DatabaseProviderSetup.Configure(o, provider, connectionString);
                return new PostgresAppDbContext(o.Options, ctx);
            }
            var s = new DbContextOptionsBuilder<AppDbContext>();
            DatabaseProviderSetup.Configure(s, provider, connectionString);
            return new AppDbContext(s.Options, ctx);
        }
    }

    private static SecurityAlert Alert(string externalId) => new()
    {
        ExternalId = externalId, AlertType = "t", Service = M365ServiceArea.EntraId, Severity = AlertSeverity.High,
        Title = "x", DetectedAt = DateTimeOffset.UtcNow, LastUpdatedAt = DateTimeOffset.UtcNow, RawJson = "{}",
    };

    /// <summary>Writes one row of every scoped entity into the given tenant's context.</summary>
    private static async Task SeedEveryScopedEntityAsync(AppDbContext db, string tag)
    {
        var policy = new AlertPolicy { Id = Guid.NewGuid(), Name = "p-" + tag, Category = "c", Condition = "x", Metric = "m", Severity = "High", CreatedAt = DateTimeOffset.UtcNow };
        db.AlertPolicies.Add(policy); // optional; TenantId null = default
        var alert = Alert("alert-" + tag);
        db.SecurityAlerts.Add(alert);
        db.CollectionRuns.Add(new CollectionRun { StartedAt = DateTimeOffset.UtcNow, Status = CollectionStatus.Completed });
        db.TriggeredAlerts.Add(new TriggeredAlert { Id = Guid.NewGuid(), PolicyId = policy.Id, PolicyName = "p", Severity = "High", Category = "c", Condition = "x", TriggeredAt = DateTimeOffset.UtcNow, Status = "Open" });
        db.NotificationLogs.Add(new NotificationLog { TriggeredAlertId = Guid.NewGuid(), PolicyName = "p", Channel = "email", Success = true });
        db.TrendSnapshots.Add(new TrendSnapshot { CapturedAt = DateTimeOffset.UtcNow });
        db.SuppressionRules.Add(new SuppressionRule { Reason = "s-" + tag, CreatedBy = "t" });
        db.AuditEvents.Add(new AuditEvent { ExternalId = "ev-" + tag, Activity = "a", OccurredAt = DateTimeOffset.UtcNow });
        // One-per-tenant rows (keyed by tenant), so only the first seed adds them.
        if (!db.TenantBaselines.Local.Any() && !await db.TenantBaselines.AnyAsync())
            db.TenantBaselines.Add(new TenantBaseline());
        if (!db.TenantNotificationRoutings.Local.Any() && !await db.TenantNotificationRoutings.AnyAsync())
            db.TenantNotificationRoutings.Add(new TenantNotificationRouting { NotifyClient = true, RecipientEmail = tag + "@client.test" });
        db.AlertPolicyTenantOverrides.Add(new AlertPolicyTenantOverride { PolicyId = policy.Id, Enabled = false, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.AlertNotes.Add(new AlertNote { TargetKind = "alert", TargetId = alert.Id.ToString(), Author = "t", Text = "n-" + tag, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task<Dictionary<Type, int>> CountEveryScopedEntityAsync(AppDbContext db)
    {
        var counts = new Dictionary<Type, int>();
        foreach (var t in TenantClassification.Scoped)
        {
            var set = (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(t).Invoke(db, null)!;
            counts[t] = await set.Cast<object>().AsNoTracking().CountAsync();
        }
        return counts;
    }

    // ── Reads ─────────────────────────────────────────────────────────────────

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Every_scoped_entity_returns_only_the_current_tenants_rows(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        await using (var a = db.Open(A)) await SeedEveryScopedEntityAsync(a, "a");
        await using (var b = db.Open(B)) { await SeedEveryScopedEntityAsync(b, "b1"); await SeedEveryScopedEntityAsync(b, "b2"); }

        await using (var a = db.Open(A))
        {
            var counts = await CountEveryScopedEntityAsync(a);
            foreach (var (type, n) in counts)
                Assert.True(n == 1, $"{type.Name}: tenant A sees {n} rows, expected 1");
        }
        await using (var b = db.Open(B))
        {
            var counts = await CountEveryScopedEntityAsync(b);
            foreach (var (type, n) in counts)
            {
                var expected = type == typeof(TenantBaseline) || type == typeof(TenantNotificationRouting) ? 1 : 2; // one-per-tenant rows (keyed by tenant)
                Assert.True(n == expected, $"{type.Name}: tenant B sees {n} rows, expected {expected}");
            }
        }
        await using (var c = db.Open(C))
        {
            var counts = await CountEveryScopedEntityAsync(c);
            foreach (var (type, n) in counts)
                Assert.True(n == 0, $"{type.Name}: tenant C sees {n} rows, expected 0");
        }
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task No_tenant_context_fails_closed_on_scoped_reads_rather_than_returning_everything(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        await using (var a = db.Open(A)) await SeedEveryScopedEntityAsync(a, "a");

        await using var none = db.Open(null);
        foreach (var t in TenantClassification.Scoped)
        {
            var set = (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(t).Invoke(none, null)!;
            await Assert.ThrowsAsync<TenantRequiredException>(() => set.Cast<object>().AsNoTracking().CountAsync());
        }
        // Global entities are unaffected.
        Assert.Equal(4, await none.ClientTenants.CountAsync()); // Default + A, B, C
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Optional_entities_return_defaults_plus_own_overrides_and_only_defaults_with_no_tenant(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        await using (var a = db.Open(A))
        {
            a.AlertPolicies.Add(new AlertPolicy { Id = Guid.NewGuid(), Name = "default", Category = "c", Condition = "x", Metric = "m", Severity = "High", CreatedAt = DateTimeOffset.UtcNow });
            a.AlertPolicies.Add(new AlertPolicy { Id = Guid.NewGuid(), Name = "a-override", TenantId = A, Category = "c", Condition = "x", Metric = "m", Severity = "High", CreatedAt = DateTimeOffset.UtcNow });
            await a.SaveChangesAsync();
        }
        await using (var b = db.Open(B))
        {
            b.AlertPolicies.Add(new AlertPolicy { Id = Guid.NewGuid(), Name = "b-override", TenantId = B, Category = "c", Condition = "x", Metric = "m", Severity = "High", CreatedAt = DateTimeOffset.UtcNow });
            await b.SaveChangesAsync();
        }

        await using (var a = db.Open(A))
            Assert.Equal(new[] { "a-override", "default" }, await a.AlertPolicies.Select(p => p.Name).OrderBy(n => n).ToListAsync());
        await using (var b = db.Open(B))
            Assert.Equal(new[] { "b-override", "default" }, await b.AlertPolicies.Select(p => p.Name).OrderBy(n => n).ToListAsync());
        await using (var c = db.Open(C))
            Assert.Equal(new[] { "default" }, await c.AlertPolicies.Select(p => p.Name).ToListAsync());
        await using (var none = db.Open(null))
            Assert.Equal(new[] { "default" }, await none.AlertPolicies.Select(p => p.Name).ToListAsync());
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Inserts_are_stamped_with_the_current_tenant(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        await using (var a = db.Open(A))
        {
            var alert = Alert("stamped");
            Assert.Equal(Guid.Empty, alert.TenantId);
            a.SecurityAlerts.Add(alert);
            await a.SaveChangesAsync();
            Assert.Equal(A, alert.TenantId);
        }
        await using (var none = db.Open(null))
            Assert.Equal(A, (await none.CrossTenant<SecurityAlert>().SingleAsync(x => x.ExternalId == "stamped")).TenantId);
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Writing_a_row_for_another_tenant_is_refused(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        await using var a = db.Open(A);

        var foreign = Alert("foreign");
        foreign.TenantId = B;
        a.SecurityAlerts.Add(foreign);
        await Assert.ThrowsAsync<CrossTenantWriteException>(() => a.SaveChangesAsync());
        a.ChangeTracker.Clear();

        var optionalForeign = new AlertPolicy { Id = Guid.NewGuid(), Name = "x", TenantId = B, Category = "c", Condition = "x", Metric = "m", Severity = "High", CreatedAt = DateTimeOffset.UtcNow };
        a.AlertPolicies.Add(optionalForeign);
        await Assert.ThrowsAsync<CrossTenantWriteException>(() => a.SaveChangesAsync());
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task A_loaded_row_cannot_be_moved_to_another_tenant(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        await using (var a = db.Open(A)) { a.SecurityAlerts.Add(Alert("move")); await a.SaveChangesAsync(); }

        await using var again = db.Open(A);
        var row = await again.SecurityAlerts.SingleAsync(x => x.ExternalId == "move");
        row.TenantId = B;
        await Assert.ThrowsAsync<CrossTenantWriteException>(() => again.SaveChangesAsync());
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Deleting_by_unloaded_stub_is_refused_so_a_foreign_id_cannot_be_deleted_blind(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        long foreignId;
        await using (var b = db.Open(B)) { var x = Alert("victim"); b.SecurityAlerts.Add(x); await b.SaveChangesAsync(); foreignId = x.Id; }

        await using (var a = db.Open(A))
        {
            a.SecurityAlerts.Remove(new SecurityAlert { Id = foreignId });
            await Assert.ThrowsAsync<CrossTenantWriteException>(() => a.SaveChangesAsync());
        }
        await using (var b = db.Open(B))
            Assert.Equal(1, await b.SecurityAlerts.CountAsync(x => x.ExternalId == "victim"));
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task No_tenant_context_fails_closed_on_scoped_writes(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        await using var none = db.Open(null);
        none.SecurityAlerts.Add(Alert("nobody"));
        await Assert.ThrowsAsync<TenantRequiredException>(() => none.SaveChangesAsync());
    }

    // ── Uniqueness and the audit chain ────────────────────────────────────────

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Two_tenants_may_hold_the_same_external_ids(DatabaseProvider provider)
    {
        // Uniqueness is per tenant: the same Graph alert id or audit record id
        // legitimately appears in two clients' data.
        var db = await SetupAsync(provider);
        foreach (var t in new[] { A, B })
        {
            await using var ctx = db.Open(t);
            ctx.SecurityAlerts.Add(Alert("same-alert"));
            ctx.AuditEvents.Add(new AuditEvent { ExternalId = "same-event", Activity = "a", OccurredAt = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync();
        }
        await using (var a = db.Open(A))
        {
            a.SecurityAlerts.Add(Alert("same-alert")); // duplicate within A still refused
            await Assert.ThrowsAsync<DbUpdateException>(() => a.SaveChangesAsync());
        }
        await using (var none = db.Open(null))
        {
            Assert.Equal(2, await none.CrossTenant<SecurityAlert>().CountAsync(x => x.ExternalId == "same-alert"));
            Assert.Equal(2, await none.CrossTenant<AuditEvent>().CountAsync(x => x.ExternalId == "same-event"));
        }
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Audit_hash_chain_stays_one_global_sequence_across_tenants(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        foreach (var t in new Guid?[] { A, B, null, C, A })
        {
            await using var ctx = db.Open(t);
            var audit = new AuditLogger(ctx, t is Guid g ? TestTenancy.For(g) : TestTenancy.None(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor(), NullLogger<AuditLogger>.Instance);
            await audit.WriteAsync("test", "thing", t?.ToString(), "d", CancellationToken.None);
        }

        await using var none = db.Open(null);
        var chain = await none.CrossTenant<AuditEntry>().AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(5, chain.Count);
        Assert.Equal(new Guid?[] { A, B, null, C, A }, chain.Select(e => e.TenantId));
        string? prev = null;
        foreach (var e in chain)
        {
            Assert.Equal(prev, e.PrevHash);
            Assert.Equal(AuditLogger.ComputeHash(e), e.EntryHash);
            prev = e.EntryHash;
        }
        // Per-tenant view: own entries plus MSP-level (null) ones.
        await using var a = db.Open(A);
        Assert.Equal(3, await a.AuditEntries.CountAsync());
    }

    // ── Workers and offboarding ───────────────────────────────────────────────

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Retention_prune_in_one_tenant_does_not_touch_another(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        foreach (var t in new[] { A, B })
        {
            await using var ctx = db.Open(t);
            var old = Alert("old"); old.IsResolved = true; old.LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-400);
            ctx.SecurityAlerts.Add(old);
            await ctx.SaveChangesAsync();
        }
        await using (var a = db.Open(A))
            Assert.Equal(1, (await DataRetentionWorker.PruneAsync(a, new RetentionOptions { ResolvedAlertsDays = 90 }, CancellationToken.None)).ResolvedAlerts);
        await using (var b = db.Open(B))
            Assert.Equal(1, await b.SecurityAlerts.CountAsync());
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Offboarding_a_tenant_removes_every_row_it_owned_and_nothing_else(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        await using (var a = db.Open(A)) await SeedEveryScopedEntityAsync(a, "a");
        await using (var b = db.Open(B)) await SeedEveryScopedEntityAsync(b, "b");

        await using (var admin = db.Open(null))
        {
            admin.ClientTenants.Remove(await admin.ClientTenants.SingleAsync(t => t.Id == A));
            await admin.SaveChangesAsync();
        }

        await using (var none = db.Open(null))
        {
            foreach (var t in TenantClassification.Scoped)
            {
                var all = (IQueryable<ITenantScoped>)typeof(AppDbContext).GetMethod(nameof(AppDbContext.CrossTenant))!.MakeGenericMethod(t).Invoke(none, null)!;
                var rows = await all.AsNoTracking().Select(r => r.TenantId).ToListAsync();
                Assert.DoesNotContain(A, rows);
                Assert.Contains(B, rows);
            }
        }
    }

    // ── The audit trail across offboarding and retention ──────────────────────

    private static AuditLogger Audit(AppDbContext ctx, Guid? t) => new(ctx, t is Guid g ? TestTenancy.For(g) : TestTenancy.None(),
        new Microsoft.AspNetCore.Http.HttpContextAccessor(), NullLogger<AuditLogger>.Instance);

    private static async Task<List<AuditEntry>> ChainAsync(Db db)
    {
        await using var none = db.Open(null);
        return await none.CrossTenant<AuditEntry>().AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Offboarding_a_tenant_keeps_its_audit_entries_and_the_chain_still_verifies(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        foreach (var t in new Guid?[] { A, B, null, A, C })
        {
            await using var ctx = db.Open(t);
            await Audit(ctx, t).WriteAsync("test", "thing", null, "d", CancellationToken.None);
        }
        await using (var a = db.Open(A))
        {
            // A's own optional rows still go with it.
            a.AlertPolicies.Add(new AlertPolicy { Id = Guid.NewGuid(), TenantId = A, Name = "a-only", Category = "c", Condition = "x", Metric = "m", Severity = "High", CreatedAt = DateTimeOffset.UtcNow });
            a.ReportSchedules.Add(new ReportSchedule { TenantId = A, Name = "a-weekly" });
            await a.SaveChangesAsync();
        }

        await using (var admin = db.Open(null))
        {
            admin.ClientTenants.Remove(await admin.ClientTenants.SingleAsync(t => t.Id == A));
            await admin.SaveChangesAsync();
        }
        // The purge itself is recorded against the client it removed.
        await using (var a = db.Open(A))
            await Audit(a, A).WriteAsync("tenant.purge", "tenant", A.ToString(), "A", CancellationToken.None);

        await using (var none = db.Open(null))
        {
            Assert.False(await none.CrossTenant<AlertPolicy>().AnyAsync(p => p.TenantId == A));
            Assert.False(await none.CrossTenant<ReportSchedule>().AnyAsync(s => s.TenantId == A));
        }
        var chain = await ChainAsync(db);
        Assert.Equal(new Guid?[] { A, B, null, A, C, A }, chain.Select(e => e.TenantId));
        Assert.Equal("tenant.purge", chain[^1].Action);
        Assert.True(AuditLogger.VerifyChain(chain).Valid);
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Rolling_back_the_audit_trail_migration_keeps_a_purged_client_s_entries(DatabaseProvider provider)
    {
        // Down() re-adds the foreign key to ClientTenants. A purged client's entries
        // name a row that no longer exists, which used to make the rollback fail.
        var db = await SetupAsync(provider);
        foreach (var t in new Guid?[] { A, B })
        {
            await using var ctx = db.Open(t);
            await Audit(ctx, t).WriteAsync("test", "thing", null, "d", CancellationToken.None);
        }
        await using (var admin = db.Open(null))
        {
            admin.ClientTenants.Remove(await admin.ClientTenants.SingleAsync(t => t.Id == A));
            await admin.SaveChangesAsync();
        }
        await using (var a = db.Open(A))
            await Audit(a, A).WriteAsync("tenant.purge", "tenant", A.ToString(), "A", CancellationToken.None);

        await using (var none = db.Open(null))
        {
            var migrator = none.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("TenantHardening");
            await migrator.MigrateAsync(); // and forward again, so the model can read the rows
        }

        var chain = await ChainAsync(db);
        Assert.Equal(new[] { "test", "test", "tenant.purge" }, chain.Select(e => e.Action));
        Assert.Equal(new Guid?[] { null, B, null }, chain.Select(e => e.TenantId)); // kept, now MSP-level
    }

    [SkippableTheory, MemberData(nameof(RelationalEngines.All), MemberType = typeof(RelationalEngines))]
    public async Task Audit_retention_prunes_a_prefix_of_the_one_chain_whatever_client_the_entries_name(DatabaseProvider provider)
    {
        var db = await SetupAsync(provider);
        var old = DateTimeOffset.UtcNow.AddDays(-400);
        var recent = DateTimeOffset.UtcNow.AddDays(-1);
        // C is deactivated, so no per-client pass would ever prune its entries;
        // the fourth entry is old but written after a recent one.
        await using (var admin = db.Open(null))
        {
            (await admin.ClientTenants.SingleAsync(t => t.Id == C)).IsActive = false;
            await admin.SaveChangesAsync();
        }
        foreach (var (t, at) in new (Guid?, DateTimeOffset)[] { (C, old), (A, old), (null, recent), (C, old), (B, recent) })
        {
            await using var ctx = db.Open(t);
            var prev = await ctx.CrossTenant<AuditEntry>().OrderByDescending(e => e.Id).Select(e => e.EntryHash).FirstOrDefaultAsync();
            var entry = new AuditEntry
            {
                TenantId = t, HashVersion = AuditLogger.CurrentHashVersion, Timestamp = AuditLogger.TruncateToMicroseconds(at),
                ActorEmail = "a@msp.test", Action = "test", TargetType = "thing", PrevHash = prev,
            };
            entry.EntryHash = AuditLogger.ComputeHash(entry);
            ctx.AuditEntries.Add(entry);
            await ctx.SaveChangesAsync();
        }

        await using (var none = db.Open(null))
            Assert.Equal(2, await DataRetentionWorker.PruneAuditEntriesAsync(none, new RetentionOptions { AuditEntriesDays = 365 }, CancellationToken.None));

        var chain = await ChainAsync(db);
        Assert.Equal(new Guid?[] { null, C, B }, chain.Select(e => e.TenantId));
        Assert.True(AuditLogger.VerifyChain(chain).Valid);
    }
}
