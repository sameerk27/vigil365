using System.Linq.Expressions;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Data;

/// <summary>
/// The application's EF model. Engine-neutral: the only provider-aware lines are
/// the two marked below, which exist because SQL Server and Postgres spell an
/// index filter and an unbounded-text column differently. Everything else is
/// plain LINQ and must stay that way — see <see cref="DatabaseProviderSetup"/>.
/// Not sealed because <see cref="PostgresAppDbContext"/> derives from it to own
/// the Postgres migration set.
///
/// Tenant isolation lives here and nowhere else. Every entity is classified in
/// <see cref="TenantClassification"/>; the interfaces on the entities drive a
/// global query filter (reads) and a SaveChanges guard (writes), both keyed on
/// the ambient <see cref="ITenantContext"/>. With no tenant, scoped reads and
/// writes throw rather than see or touch every tenant's rows. The only way to
/// read across tenants is <see cref="CrossTenant{T}"/>, so every such read is
/// greppable.
/// </summary>
public class AppDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public AppDbContext(DbContextOptions options, ITenantContext? tenant = null) : base(options)
    {
        _tenant = tenant ?? NoTenantContext.Instance;
    }

    /// <summary>The current tenant, or a <see cref="TenantRequiredException"/>. Referenced by the scoped query filters.</summary>
    public Guid CurrentTenantId => _tenant.Current ?? throw new TenantRequiredException("a tenant-scoped query");

    /// <summary>The current tenant or null. Referenced by the optional (default-or-override) filters.</summary>
    public Guid? CurrentTenantIdOrNull => _tenant.Current;

    /// <summary>
    /// The single sanctioned cross-tenant read. Use it for MSP rollups and the
    /// audit hash chain; each call site should be able to say why it needs every
    /// tenant's rows. A test asserts IgnoreQueryFilters appears nowhere else.
    /// </summary>
    public IQueryable<T> CrossTenant<T>() where T : class => Set<T>().IgnoreQueryFilters();

    public DbSet<ClientTenant> ClientTenants => Set<ClientTenant>();
    public DbSet<UserTenantAssignment> UserTenantAssignments => Set<UserTenantAssignment>();
    public DbSet<TenantNotificationRouting> TenantNotificationRoutings => Set<TenantNotificationRouting>();
    public DbSet<AlertPolicyTenantOverride> AlertPolicyTenantOverrides => Set<AlertPolicyTenantOverride>();
    public DbSet<SecurityAlert> SecurityAlerts => Set<SecurityAlert>();
    public DbSet<CollectionRun> CollectionRuns => Set<CollectionRun>();
    public DbSet<AlertPolicy> AlertPolicies => Set<AlertPolicy>();
    public DbSet<TriggeredAlert> TriggeredAlerts => Set<TriggeredAlert>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<TrendSnapshot> TrendSnapshots => Set<TrendSnapshot>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<GraphConfig> GraphConfig => Set<GraphConfig>();
    public DbSet<AlertNote> AlertNotes => Set<AlertNote>();
    public DbSet<SuppressionRule> SuppressionRules => Set<SuppressionRule>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<TenantBaseline> TenantBaselines => Set<TenantBaseline>();
    public DbSet<MetricsCounters> MetricsCounters => Set<MetricsCounters>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Provider-specific spellings. Postgres quotes identifiers with double
        // quotes and calls unbounded text "text"; SQL Server uses brackets and
        // nvarchar(max). The SQL Server branch is kept byte-identical to what the
        // existing migrations were generated from so its model snapshot does not
        // move. Anything not SQL Server or Postgres (the in-memory test provider)
        // takes the SQL Server branch, which it ignores.
        var isPostgres = Database.IsNpgsql();
        var externalIdNotNull = isPostgres ? "\"ExternalId\" IS NOT NULL" : "[ExternalId] IS NOT NULL";
        var unboundedText = isPostgres ? "text" : "nvarchar(max)";

        modelBuilder.Entity<ClientTenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.MicrosoftTenantId);
            entity.HasIndex(t => t.IsActive);
            // Every pre-tenancy install is migrated into this row, and a
            // single-tenant install keeps using it. Fixed values only — HasData
            // is compared into the model snapshot.
            entity.HasData(new ClientTenant
            {
                Id = ClientTenant.DefaultId,
                Name = "Default",
                IsActive = true,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        });

        modelBuilder.Entity<UserTenantAssignment>(entity =>
        {
            entity.HasKey(a => new { a.UserEmail, a.TenantId });
            entity.HasIndex(a => a.TenantId);
            // Removing a client removes its assignments; removing a user removes theirs.
            entity.HasOne<ClientTenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserEmail).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SecurityAlert>(entity =>
        {
            // Uniqueness is per tenant: two clients can legitimately report the
            // same Graph alert id.
            entity.HasIndex(a => new { a.TenantId, a.Service, a.AlertType, a.ExternalId }).IsUnique().HasFilter(externalIdNotNull);
            entity.HasIndex(a => a.DetectedAt);
            entity.HasIndex(a => new { a.Service, a.Severity, a.IsResolved });
            entity.Property(a => a.AlertType).HasMaxLength(120);
            entity.Property(a => a.RawJson).HasColumnType(unboundedText);
        });

        modelBuilder.Entity<CollectionRun>(entity =>
        {
            entity.HasIndex(r => r.StartedAt);
            entity.Property(r => r.Error).HasMaxLength(4000);
            entity.Property(r => r.SourceFailureDetails).HasColumnType(unboundedText);
        });

        modelBuilder.Entity<AlertPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.Enabled);
        });

        modelBuilder.Entity<TriggeredAlert>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.TriggeredAt);
            entity.HasIndex(t => t.Status);
            entity.HasIndex(t => t.PolicyId);
        });

        modelBuilder.Entity<SuppressionRule>(entity =>
        {
            entity.HasKey(s => s.Id);
            // The evaluator loads enabled rules on every cycle.
            entity.HasIndex(s => s.Enabled);
            entity.HasIndex(s => s.PolicyId);
        });

        modelBuilder.Entity<NotificationSettings>(entity =>
        {
            entity.HasKey(s => s.Id);
            // Singleton row with a fixed key of 1 (see the model's default). EF's
            // convention would make an int key an identity column, and then the
            // explicit 1 gets sent in the INSERT and SQL Server rejects it —
            // which crashed every fresh install on its first startup write.
            entity.Property(s => s.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<NotificationLog>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => l.SentAt);
        });

        modelBuilder.Entity<TrendSnapshot>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.CapturedAt);
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(u => u.Email);
            entity.Property(u => u.Email).HasMaxLength(320);
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => a.Timestamp);
        });

        modelBuilder.Entity<GraphConfig>(entity =>
        {
            entity.HasKey(g => g.Id);
            // Singleton row with a fixed key of 1, as for NotificationSettings
            // above. Left as an identity column this fails the moment someone
            // saves Graph credentials on the setup page.
            entity.Property(g => g.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<AlertNote>(entity =>
        {
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => new { n.TargetKind, n.TargetId });
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TenantId, e.Source, e.ExternalId }).IsUnique();
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.Activity);
            entity.Property(e => e.RawJson).HasColumnType(unboundedText);
        });

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => t.Prefix);
            entity.HasIndex(t => t.RevokedAt);
            // A token restricted to a client dies with the client.
            entity.HasOne<ClientTenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantNotificationRouting>(entity =>
        {
            entity.HasKey(r => r.TenantId); // one per tenant
        });

        modelBuilder.Entity<AlertPolicyTenantOverride>(entity =>
        {
            entity.HasKey(o => new { o.TenantId, o.PolicyId });
            // Restrict, not cascade: SQL Server refuses a second cascade path (the
            // tenant FK already cascades, and AlertPolicy itself cascades from the
            // tenant). The policy DELETE endpoint removes overrides explicitly.
            entity.HasOne<AlertPolicy>().WithMany().HasForeignKey(o => o.PolicyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantBaseline>(entity =>
        {
            // One baseline per tenant, keyed by the tenant.
            entity.HasKey(b => b.TenantId);
        });

        modelBuilder.Entity<MetricsCounters>(entity =>
        {
            entity.HasKey(c => c.Id);
            // Singleton row with a fixed key of 1 — see the note above.
            entity.Property(c => c.Id).ValueGeneratedNever();
        });

        ApplyTenantFilters(modelBuilder);
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    /// <summary>
    /// Scoped entities: TenantId == CurrentTenantId (throws with no tenant).
    /// Optional entities: TenantId IS NULL OR TenantId == current (null-safe).
    /// Every scoped/optional entity also gets a TenantId index and a foreign key
    /// to ClientTenant that cascades on tenant deletion — offboarding a client
    /// provably removes their rows. AuditEntry alone has no foreign key.
    /// </summary>
    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        var self = Expression.Constant(this);
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            var e = Expression.Parameter(clr, "e");

            if (typeof(ITenantScoped).IsAssignableFrom(clr))
            {
                var tenantId = Expression.Property(e, nameof(ITenantScoped.TenantId));
                var body = Expression.Equal(tenantId, Expression.Property(self, nameof(CurrentTenantId)));
                var builder = modelBuilder.Entity(clr);
                builder.HasQueryFilter(Expression.Lambda(body, e));
                builder.HasIndex("TenantId");
                builder.HasOne(typeof(ClientTenant)).WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Cascade);
            }
            else if (typeof(ITenantOptional).IsAssignableFrom(clr))
            {
                var tenantId = Expression.Property(e, nameof(ITenantOptional.TenantId));
                var isDefault = Expression.Equal(tenantId, Expression.Constant(null, typeof(Guid?)));
                var isMine = Expression.Equal(tenantId, Expression.Property(self, nameof(CurrentTenantIdOrNull)));
                var builder = modelBuilder.Entity(clr);
                builder.HasQueryFilter(Expression.Lambda(Expression.OrElse(isDefault, isMine), e));
                builder.HasIndex("TenantId");
                // The audit trail is the MSP's record and outlives the client: no
                // foreign key, so purging a client neither deletes its entries nor
                // breaks the one global hash chain they are links of.
                if (clr != typeof(AuditEntry))
                    builder.HasOne(typeof(ClientTenant)).WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Cascade);
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantOnWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantOnWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Added scoped rows are stamped with the current tenant; an added, modified
    /// or deleted scoped row carrying any other tenant is refused. Deleting via
    /// an unloaded stub (TenantId empty) is refused too — load it through the
    /// filter first. Optional rows are never stamped (null means "default") but
    /// may not name a foreign tenant.
    /// </summary>
    private void EnforceTenantOnWrites()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            switch (entry.Entity)
            {
                case ITenantScoped scoped:
                {
                    var current = _tenant.Current ?? throw new TenantRequiredException($"writing {entry.Metadata.ClrType.Name}");
                    if (entry.State == EntityState.Added && scoped.TenantId == Guid.Empty)
                        scoped.TenantId = current;
                    if (scoped.TenantId != current)
                        throw new CrossTenantWriteException(entry.Metadata.ClrType.Name, scoped.TenantId, current);
                    break;
                }
                case ITenantOptional optional when optional.TenantId is Guid t:
                {
                    var current = _tenant.Current;
                    if (current is null || t != current)
                        throw new CrossTenantWriteException(entry.Metadata.ClrType.Name, t, current ?? Guid.Empty);
                    break;
                }
            }
        }
    }
}
