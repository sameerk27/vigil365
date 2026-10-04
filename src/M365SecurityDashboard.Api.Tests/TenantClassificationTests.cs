using M365SecurityDashboard.Api.Data;
using M365SecurityDashboard.Api.Data.Tenancy;
using M365SecurityDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Holds the model to its tenancy classification. These need no database: they
/// inspect the model and the source tree, and they fail the moment someone adds
/// an entity without deciding whose data it is, or reaches for
/// IgnoreQueryFilters outside the one sanctioned seam.
/// </summary>
public sealed class TenantClassificationTests
{
    [Fact]
    public void Every_entity_is_classified_exactly_once()
    {
        using var db = TestAppDbContextFactory.Create();
        var entities = db.Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();

        var classified = TenantClassification.Scoped
            .Concat(TenantClassification.Optional)
            .Concat(TenantClassification.Global)
            .ToList();

        Assert.Equal(classified.Count, classified.Distinct().Count()); // no type in two lists
        var unclassified = entities.Except(classified).Select(t => t.Name).OrderBy(n => n).ToList();
        Assert.True(unclassified.Count == 0,
            $"Unclassified entities (add to TenantClassification): {string.Join(", ", unclassified)}");
        var stale = classified.Except(entities).Select(t => t.Name).ToList();
        Assert.True(stale.Count == 0, $"Classified but not in the model: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Classification_matches_the_interfaces_the_context_enforces()
    {
        // The list is what a reviewer reads; the interfaces are what runs. They
        // must agree, or one of them is lying.
        foreach (var t in TenantClassification.Scoped)
            Assert.True(typeof(ITenantScoped).IsAssignableFrom(t), $"{t.Name} is listed as scoped but does not implement ITenantScoped");
        foreach (var t in TenantClassification.Optional)
            Assert.True(typeof(ITenantOptional).IsAssignableFrom(t), $"{t.Name} is listed as optional but does not implement ITenantOptional");
        foreach (var t in TenantClassification.Global)
        {
            Assert.False(typeof(ITenantScoped).IsAssignableFrom(t), $"{t.Name} is listed as global but implements ITenantScoped");
            Assert.False(typeof(ITenantOptional).IsAssignableFrom(t), $"{t.Name} is listed as global but implements ITenantOptional");
        }
    }

    [Fact]
    public void Scoped_and_optional_entities_have_a_query_filter_and_global_ones_do_not()
    {
        using var db = TestAppDbContextFactory.Create();
        foreach (var et in db.Model.GetEntityTypes())
        {
            var hasFilter = et.GetQueryFilter() is not null;
            var shouldHave = TenantClassification.Scoped.Contains(et.ClrType) || TenantClassification.Optional.Contains(et.ClrType);
            Assert.True(hasFilter == shouldHave, $"{et.ClrType.Name}: filter present={hasFilter}, expected={shouldHave}");
        }
    }

    [Fact]
    public void Purging_a_client_cascades_to_its_rows_but_not_to_the_audit_trail()
    {
        // The audit entries are links of one global hash chain and the MSP's own
        // record: deleting a client's from the middle would read as tampering.
        using var db = TestAppDbContextFactory.Create();
        foreach (var t in TenantClassification.Scoped.Concat(TenantClassification.Optional))
        {
            var toTenant = db.Model.FindEntityType(t)!.GetForeignKeys()
                .Where(fk => fk.PrincipalEntityType.ClrType == typeof(ClientTenant)).ToList();
            if (t == typeof(AuditEntry))
                Assert.True(toTenant.Count == 0, "AuditEntry must have no foreign key to ClientTenant");
            else
                Assert.True(toTenant.Count == 1 && toTenant[0].DeleteBehavior == DeleteBehavior.Cascade, $"{t.Name} must cascade from ClientTenant");
        }
    }

    [Fact]
    public void IgnoreQueryFilters_appears_only_in_AppDbContext()
    {
        // Every cross-tenant read goes through AppDbContext.CrossTenant<T>() so
        // that "grep IgnoreQueryFilters" finds exactly one place.
        var apiDir = FindApiSourceDir();
        var offenders = Directory.EnumerateFiles(apiDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("IgnoreQueryFilters"))
            .Select(f => Path.GetRelativePath(apiDir, f))
            .ToList();

        Assert.Equal(new[] { Path.Combine("Data", "AppDbContext.cs") }, offenders);
    }

    [Fact]
    public void Find_is_not_used_on_tenant_entities()
    {
        // DbSet.Find/FindAsync bypass global query filters. Any lookup by key on
        // a scoped or optional entity must go through a filtered query.
        var apiDir = FindApiSourceDir();
        var offenders = Directory.EnumerateFiles(apiDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (file: Path.GetRelativePath(apiDir, f), line, no: i + 1)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line, @"\bdb\.\w+\.Find(Async)?\("))
            .Select(x => $"{x.file}:{x.no}")
            .ToList();

        Assert.True(offenders.Count == 0, "Find/FindAsync on a DbSet: " + string.Join(", ", offenders));
    }

    private static string FindApiSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "M365SecurityDashboard.Api");
            if (File.Exists(Path.Combine(candidate, "Program.cs"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the M365SecurityDashboard.Api source directory from " + AppContext.BaseDirectory);
    }
}
