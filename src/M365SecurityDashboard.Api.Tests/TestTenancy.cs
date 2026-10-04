using M365SecurityDashboard.Api.Data.Tenancy;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>Well-known tenant ids for tests, and the context that selects one.</summary>
internal static class TestTenancy
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    public static readonly Guid TenantC = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    /// <summary>The tenant every ordinary (in-memory) test runs in.</summary>
    public static readonly Guid Default = TenantA;

    public static TenantContext For(Guid tenant) => new(tenant);
    public static TenantContext None() => new();
}
