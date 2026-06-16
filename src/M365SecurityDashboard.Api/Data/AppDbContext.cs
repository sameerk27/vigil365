using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SecurityAlert> SecurityAlerts => Set<SecurityAlert>();
    public DbSet<CollectionRun> CollectionRuns => Set<CollectionRun>();

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
    }
}
