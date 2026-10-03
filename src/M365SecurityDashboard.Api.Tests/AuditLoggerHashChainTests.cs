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
        new(db, TestTenancy.For(TestTenancy.Default), new HttpContextAccessor(), NullLogger<AuditLogger>.Instance);

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

    [Fact]
    public async Task New_entries_bind_their_client_into_the_hash()
    {
        using var db = TestAppDbContextFactory.Create();
        await CreateLogger(db).WriteAsync("alert.resolve", "triggered_alert", "7", "resolved", CancellationToken.None);
        var entry = await db.AuditEntries.SingleAsync();
        Assert.Equal(AuditLogger.CurrentHashVersion, entry.HashVersion);
        Assert.Equal(TestTenancy.Default, entry.TenantId);
        Assert.Equal(entry.EntryHash, AuditLogger.ComputeHash(entry));

        // Moving the entry to another client, or to MSP-level, is tampering.
        entry.TenantId = TestTenancy.TenantB;
        Assert.NotEqual(entry.EntryHash, AuditLogger.ComputeHash(entry));
        entry.TenantId = null;
        Assert.NotEqual(entry.EntryHash, AuditLogger.ComputeHash(entry));
        // So is relabelling it as an original-form entry, which ignores the client.
        entry.TenantId = TestTenancy.Default;
        entry.HashVersion = 0;
        Assert.NotEqual(entry.EntryHash, AuditLogger.ComputeHash(entry));
    }

    [Fact]
    public void Entries_hashed_before_the_client_was_covered_still_verify()
    {
        // HashVersion 0 is every entry written before 1.2.0. Its canonical form is
        // pinned here, computed independently: changing it breaks existing chains.
        static string OriginalForm(AuditEntry e) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\u001f', e.PrevHash ?? "", e.Timestamp.UtcDateTime.ToString("O"),
                e.ActorEmail, e.Action, e.TargetType, e.TargetId ?? "", e.Details ?? "", e.IpAddress ?? "", e.UserAgent ?? ""))));

        var at = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var old1 = new AuditEntry { Id = 1, TenantId = TestTenancy.TenantA, Timestamp = at, ActorEmail = "a@msp.test", Action = "user.add", TargetType = "user", TargetId = "b@msp.test" };
        old1.EntryHash = OriginalForm(old1);
        var old2 = new AuditEntry { Id = 2, Timestamp = at.AddMinutes(1), ActorEmail = "a@msp.test", Action = "alert.resolve", TargetType = "triggered_alert", PrevHash = old1.EntryHash, IpAddress = "10.0.0.1" };
        old2.EntryHash = OriginalForm(old2);
        // ...followed by an entry written after the upgrade.
        var current = new AuditEntry { Id = 3, TenantId = TestTenancy.TenantB, HashVersion = AuditLogger.CurrentHashVersion, Timestamp = at.AddMinutes(2), ActorEmail = "a@msp.test", Action = "tenant.purge", TargetType = "tenant", PrevHash = old2.EntryHash };
        current.EntryHash = AuditLogger.ComputeHash(current);

        Assert.Equal(old1.EntryHash, AuditLogger.ComputeHash(old1));
        var result = AuditLogger.VerifyChain([old1, old2, current]);
        Assert.True(result.Valid);
        Assert.Equal(3, result.Verified);

        current.TenantId = TestTenancy.TenantA;
        Assert.Equal(3, AuditLogger.VerifyChain([old1, old2, current]).FirstBrokenId);
    }

    [Fact]
    public async Task A_pruned_prefix_still_verifies_but_a_hole_in_the_middle_does_not()
    {
        using var db = TestAppDbContextFactory.Create();
        var logger = CreateLogger(db);
        for (var i = 0; i < 4; i++)
            await logger.WriteAsync("x", "y", i.ToString(), null, CancellationToken.None);
        var chain = await db.AuditEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();

        Assert.True(AuditLogger.VerifyChain(chain).Valid);
        Assert.True(AuditLogger.VerifyChain(chain.Skip(2).ToList()).Valid); // retention removed the oldest two
        var holed = chain.Where((_, i) => i != 1).ToList();                  // one entry deleted from the middle
        Assert.Equal(chain[2].Id, AuditLogger.VerifyChain(holed).FirstBrokenId);
    }

    [Fact]
    public async Task A_caller_supplied_X_Forwarded_For_is_not_recorded_as_the_client_ip()
    {
        // Behind a trusted proxy the forwarded-headers middleware has already put the
        // client's address on the connection; a header reaching here is unverified.
        using var db = TestAppDbContextFactory.Create();
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        http.Request.Headers["X-Forwarded-For"] = "10.0.0.1";
        var logger = new AuditLogger(db, TestTenancy.For(TestTenancy.Default), new HttpContextAccessor { HttpContext = http }, NullLogger<AuditLogger>.Instance);

        await logger.WriteAsync("user.add", "user", "a@contoso.com", "added", CancellationToken.None);

        Assert.Equal("203.0.113.9", (await db.AuditEntries.SingleAsync()).IpAddress);
    }

    [Fact]
    public void Timestamps_are_truncated_to_microseconds_so_the_hash_survives_a_postgres_round_trip()
    {
        var withTicks = new DateTimeOffset(2026, 10, 2, 9, 30, 15, TimeSpan.Zero).AddTicks(1234567);
        var stored = AuditLogger.TruncateToMicroseconds(withTicks);
        Assert.Equal(0, stored.Ticks % 10);
        Assert.Equal(withTicks.Ticks - 7, stored.Ticks);

        // What Postgres hands back is exactly what was stored, so the hash matches.
        var entry = new AuditEntry { Timestamp = stored, ActorEmail = "a", Action = "x", TargetType = "y" };
        var hash = AuditLogger.ComputeHash(entry);
        var readBack = new AuditEntry { Timestamp = new DateTimeOffset(stored.Ticks - stored.Ticks % 10, TimeSpan.Zero), ActorEmail = "a", Action = "x", TargetType = "y" };
        Assert.Equal(hash, AuditLogger.ComputeHash(readBack));
    }
}
