using M365SecurityDashboard.Api.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace M365SecurityDashboard.Api.Data;

/// <summary>
/// The PostgreSQL flavour of <see cref="AppDbContext"/>. It adds no behaviour;
/// it exists only so that EF Core can keep a separate migration history for
/// Postgres inside this one assembly.
///
/// EF matches migrations to a context by the <c>[DbContext(typeof(...))]</c>
/// attribute on each migration class, with an exact type comparison. The 15
/// existing SQL Server migrations are attributed to <see cref="AppDbContext"/>
/// and must stay that way — every production install has those ids recorded in
/// <c>__EFMigrationsHistory</c>. Postgres migrations are attributed to this
/// type instead, so each engine sees only its own set and neither can be
/// applied to the wrong database by accident.
///
/// Consumers never see this type: DI registers it as the implementation behind
/// <see cref="AppDbContext"/> when Database:Provider is Postgres.
/// </summary>
public sealed class PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options, ITenantContext? tenant = null)
    : AppDbContext(options, tenant);
