using M365SecurityDashboard.Api.Models;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

public class EntityProfileBuilderTests
{
    private static SecurityAlert Alert(string? upn, string? device, AlertSeverity sev, bool resolved, DateTimeOffset at) => new()
    {
        ExternalId = Guid.NewGuid().ToString(), AlertType = "t", Service = M365ServiceArea.DefenderXdr,
        Severity = sev, Title = "Alert", UserPrincipalName = upn, DeviceName = device,
        DetectedAt = at, LastUpdatedAt = at, IsResolved = resolved,
    };

    private static AuditEvent Audit(string activity, string? actor, string? target, DateTimeOffset at, string? result = "success") => new()
    {
        ExternalId = Guid.NewGuid().ToString(), Activity = activity, ActorUpn = actor, TargetName = target,
        OccurredAt = at, CollectedAt = at, Result = result,
    };

    [Fact]
    public async Task BuildAsync_User_MergesAlertsAndActivityNewestFirst()
    {
        using var db = TestAppDbContextFactory.Create();
        var t0 = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        db.SecurityAlerts.Add(Alert("bob@x.com", null, AlertSeverity.High, resolved: false, t0.AddHours(1)));
        db.SecurityAlerts.Add(Alert("other@x.com", null, AlertSeverity.Critical, resolved: false, t0.AddHours(5))); // different user
        db.AuditEvents.Add(Audit("Add member to role", actor: "bob@x.com", target: "Global Admin", t0.AddHours(3)));
        db.AuditEvents.Add(Audit("Reset password", actor: "admin@x.com", target: "bob@x.com", t0.AddHours(2)));
        await db.SaveChangesAsync();

        var p = await new EntityProfileBuilder(db).BuildAsync("user", "bob@x.com", 300, CancellationToken.None);

        Assert.True(p.Found);
        Assert.Equal(1, p.Summary.AlertCount);        // only bob's alert
        Assert.Equal(1, p.Summary.OpenAlertCount);
        Assert.Equal(2, p.Summary.ActivityCount);     // acted + targeted
        Assert.Equal(3, p.Timeline.Count);
        // Newest first: the role add at +3h leads.
        Assert.Equal(t0.AddHours(3), p.Timeline[0].At);
        Assert.Equal("activity", p.Timeline[0].Type);
    }

    [Fact]
    public async Task BuildAsync_Device_UsesDeviceNameAndHasNoActorActivity()
    {
        using var db = TestAppDbContextFactory.Create();
        var t0 = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        db.SecurityAlerts.Add(Alert(null, "LAPTOP-01", AlertSeverity.Medium, resolved: true, t0));
        db.AuditEvents.Add(Audit("Update device", actor: "admin@x.com", target: "LAPTOP-01", t0.AddHours(1)));
        await db.SaveChangesAsync();

        var p = await new EntityProfileBuilder(db).BuildAsync("device", "LAPTOP-01", 300, CancellationToken.None);

        Assert.Equal(1, p.Summary.AlertCount);
        Assert.Equal(0, p.Summary.OpenAlertCount);   // resolved
        Assert.Equal(1, p.Summary.ActivityCount);    // matched by target name
        Assert.Equal("device", p.Summary.Kind);
    }

    [Fact]
    public async Task BuildAsync_UnknownEntity_NotFoundButEmptyProfile()
    {
        using var db = TestAppDbContextFactory.Create();
        var p = await new EntityProfileBuilder(db).BuildAsync("user", "ghost@x.com", 300, CancellationToken.None);
        Assert.False(p.Found);
        Assert.Empty(p.Timeline);
        Assert.Null(p.Summary.FirstSeen);
    }

    [Fact]
    public async Task BuildAsync_RespectsMaxItemsCap()
    {
        using var db = TestAppDbContextFactory.Create();
        var t0 = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 10; i++) db.AuditEvents.Add(Audit($"act {i}", "bob@x.com", null, t0.AddMinutes(i)));
        await db.SaveChangesAsync();

        var p = await new EntityProfileBuilder(db).BuildAsync("user", "bob@x.com", 5, CancellationToken.None);
        Assert.Equal(5, p.Timeline.Count);
        Assert.Equal(10, p.Summary.ActivityCount);   // summary counts all, timeline is capped
    }
}
