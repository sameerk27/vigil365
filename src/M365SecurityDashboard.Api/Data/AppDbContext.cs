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

        modelBuilder.Entity<NotificationSettings>(entity =>
        {
            entity.HasKey(s => s.Id);
        });

        modelBuilder.Entity<NotificationLog>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => l.SentAt);
        });
    }
}
