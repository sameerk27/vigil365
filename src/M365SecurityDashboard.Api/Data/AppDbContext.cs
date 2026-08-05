using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SecurityAlert>(entity =>
        {
            entity.HasIndex(a => new { a.Service, a.AlertType, a.ExternalId }).IsUnique().HasFilter("[ExternalId] IS NOT NULL");
            entity.HasIndex(a => a.DetectedAt);
            entity.HasIndex(a => new { a.Service, a.Severity, a.IsResolved });
            entity.Property(a => a.AlertType).HasMaxLength(120);
            entity.Property(a => a.RawJson).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<CollectionRun>(entity =>
        {
            entity.HasIndex(r => r.StartedAt);
            entity.Property(r => r.Error).HasMaxLength(4000);
            entity.Property(r => r.SourceFailureDetails).HasColumnType("nvarchar(max)");
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
            entity.HasIndex(e => new { e.Source, e.ExternalId }).IsUnique();
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.Activity);
            entity.Property(e => e.RawJson).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => t.Prefix);
            entity.HasIndex(t => t.RevokedAt);
        });
    }
}
