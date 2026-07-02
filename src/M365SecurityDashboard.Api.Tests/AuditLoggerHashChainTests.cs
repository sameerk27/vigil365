using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class AuditLoggerHashChainTests
{
    private static AuditLogger CreateLogger(Data.AppDbContext db) =>
        new(db, new HttpContextAccessor(), NullLogger<AuditLogger>.Instance);

    [Fact]
    public async Task WriteAsync_ChainsEntriesByPrevHash()
    {
        using var db = TestAppDbContextFactory.Create();
        var logger = CreateLogger(db);

        await logger.WriteAsync("user.add", "user", "a@contoso.com", "added", CancellationToken.None);
        await logger.WriteAsync("user.role_change", "user", "a@contoso.com", "Viewer -> Admin", CancellationToken.None);

        var entries = await db.AuditEntries.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Null(entries[0].PrevHash);
        Assert.NotNull(entries[0].EntryHash);
        Assert.Equal(entries[0].EntryHash, entries[1].PrevHash);
        Assert.Equal(AuditLogger.ComputeHash(entries[0]), entries[0].EntryHash);
        Assert.Equal(AuditLogger.ComputeHash(entries[1]), entries[1].EntryHash);
    }

    [Fact]
    public async Task TamperedDetails_ChangesComputedHash()
    {
        using var db = TestAppDbContextFactory.Create();
        var logger = CreateLogger(db);

        await logger.WriteAsync("policy.delete", "policy", "42", "Deleted policy X", CancellationToken.None);
        var entry = await db.AuditEntries.SingleAsync();
        var originalHash = entry.EntryHash;

        entry.Details = "Deleted policy Y";
        Assert.NotEqual(originalHash, AuditLogger.ComputeHash(entry));
    }

    [Fact]
    public async Task WriteAsync_WithoutHttpContext_RecordsSystemActor()
    {
        using var db = TestAppDbContextFactory.Create();
        var logger = CreateLogger(db);

        await logger.WriteAsync("retention.prune", "database", null, "pruned 10 rows", CancellationToken.None);

        var entry = await db.AuditEntries.SingleAsync();
        Assert.Equal("system", entry.ActorEmail);
        Assert.Null(entry.IpAddress);
    }
}
