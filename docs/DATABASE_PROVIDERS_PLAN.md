# Vigil365 — Multi-Database Provider Plan

**Decision:** Vigil365 must run on more than SQL Server. PostgreSQL is the first
additional provider; the abstraction should not assume it is the last.

**Why:** the MSP edition (`MSP_MULTITENANT_PLAN.md`) makes SQL Server licensing a
per-MSP cost on data that grows with client count, and SQL Express's 10 GB /
1.4 GB-buffer-pool ceiling is reached at a few dozen tenants. Postgres removes the
licensing question and the artificial ceiling, and it is the default expectation for
self-hosters on Linux/Docker.

---

## 0. What the audit found

The provider-specific surface is **small and well-contained**. It is not spread
through the query layer.

| Site | What it is | Portability cost |
|---|---|---|
| `Program.cs:72` | the single `UseSqlServer(...)` call | trivial — becomes a switch |
| `Program.cs:155-178` | legacy-DB detection (`OBJECT_ID`) + `__EFMigrationsHistory` baselining | **none — gate to SQL Server** (see §2) |
| `Data/AlertingSchema.cs` | 286 lines of idempotent T-SQL DDL | **none — gate to SQL Server** (see §2) |
| `Services/MetricsService.cs:107-124` | DB size via `sys.database_files` | small — one query per provider |
| `Data/AppDbContext.cs` (3 sites) | `HasColumnType("nvarchar(max)")` | small — drop or make conditional |
| `Data/Migrations/*` (15 migrations) | SQL Server-shaped DDL | **the bulk of the work** — needs a parallel set |

Two findings that materially reduce risk:

- **Every temporal property is `DateTimeOffset` — there are zero bare `DateTime`
  properties in the model.** This is the single biggest thing that usually breaks a
  Postgres port (`DateTime.Kind` ambiguity, `timestamp` vs `timestamptz`). Npgsql maps
  `DateTimeOffset` to `timestamptz` cleanly. This port starts from the good case.
- **Only three raw-SQL sites exist**, and two of them are on a code path that a
  Postgres install can never reach.

---

## 1. Target shape

A `DatabaseOptions` config section selects the provider; the connection string is
unchanged in spirit.

```json
"Database": { "Provider": "SqlServer" },   // or "Postgres"
"ConnectionStrings": { "DefaultConnection": "..." }
```

`Provider` defaults to `SqlServer` so **every existing install keeps working with no
config change**. Provider selection happens in exactly one place; nothing downstream
branches on it except the two explicitly provider-shaped helpers in §3.

---

## 2. The scope reduction that makes this cheap

`AlertingSchema.EnsureTablesSql` and the `OBJECT_ID` legacy probe exist to rescue
**pre-migration SQL Server databases** created before the migration baseline. A
PostgreSQL database is, by definition, always a fresh install — there is no legacy
Postgres estate to rescue, and there never will be.

**Therefore: do not port them.** Gate both behind `if (provider == SqlServer)`. This
removes ~290 lines of T-SQL from the porting surface and is correct, not a shortcut —
porting them would produce dead code that can never execute.

Consequence: on Postgres the schema comes from EF migrations alone, which is the
cleaner path we would have wanted anyway.

---

## 3. Provider-shaped seams

Only two pieces of behaviour genuinely differ and must be abstracted rather than
gated. Introduce a small `IDatabaseProviderAdapter` with one implementation per
provider:

1. **Database size** (`MetricsService`) — SQL Server sums `sys.database_files`;
   Postgres uses `pg_database_size(current_database())`. Same contract, returns
   `long?`, null when unavailable.
2. **Large-text column type** — replace the three `HasColumnType("nvarchar(max)")`
   calls. Preferred fix is to delete them and let each provider's default for an
   unbounded `string` apply (`nvarchar(max)` / `text`); use the adapter only if a
   migration diff shows the default is wrong.

Resist adding anything else to this interface. If a third seam appears, that is a
signal the query layer is drifting provider-specific and should be pushed back into
LINQ.

---

## 4. Migrations

This is the real work. EF Core cannot share one migration set across providers.

- Split into per-provider assemblies/directories: `Data/Migrations/SqlServer/` and
  `Data/Migrations/Postgres/`, each with its own `DbContext` factory for design time.
- **SQL Server keeps its existing 15 migrations untouched.** Do not regenerate them —
  regenerating risks disturbing the baseline that the legacy-rescue path in §2 pins to,
  and existing installs have those `MigrationId`s recorded in `__EFMigrationsHistory`.
- Postgres gets **one squashed `InitialCreate`** generated from the current model. It
  has no history to preserve, so replaying 15 historical migrations would be pure cost.
- From here on, every model change generates **two** migrations. Add a CI check that
  fails when the two sets drift out of sync — this is the ongoing tax of the decision
  and it must be enforced mechanically, not by discipline.

---

## 5. Testing (the part that currently does not exist)

The test suite uses `UseInMemoryDatabase` throughout (4 sites). **In-memory is not a
relational provider — it proves nothing about either SQL Server or Postgres behaviour**
and will happily pass queries that fail on both.

- Add Testcontainers-based integration tests that run the same suite against real SQL
  Server and real Postgres containers.
- Make the migration-applies-cleanly check part of it for both providers.
- This matters far beyond this plan: the MSP edition's **cross-tenant isolation tests
  (`MSP_MULTITENANT_PLAN.md` §2) are worthless on the in-memory provider**, because EF
  global query filters need real SQL generation to be meaningfully verified. Building
  the real-provider harness here is a prerequisite for that work, not a detour.

---

## 6. Sequencing against the MSP edition

Do this **before** MSP Phase 1 (`TenantId` + global query filter + isolation tests).

The earlier instinct was to defer it, on the grounds that Phase 1 is provider-agnostic.
That is true of the *schema* change but false of the *tests*: the isolation tests are
the deliverable of Phase 1, they must run on a real provider, and they must pass on
every provider that ships. Adding Postgres afterwards means re-validating every
isolation guarantee a second time — exactly the duplicated work deferring was meant to
avoid.

Order:

1. Provider switch + gate the legacy path + adapter seams (§1-3).
2. Per-provider migration sets + CI drift check (§4).
3. Testcontainers harness, both providers green (§5).
4. **Then** MSP Phase 1, written once and verified on both.

## 7. Effort

Steps 1-3 are on the order of a few days, dominated by the migration split and the
test harness — not the provider switch itself. The genuine long-term cost is the
**permanent dual-migration tax** in §4, which is the price of the decision and should
be automated on day one.

## 8. Non-goals

- **Not** supporting SQLite or MySQL now. The seams in §3 keep them cheap to add later;
  building for them speculatively is not justified.
- **Not** abstracting behind a repository layer or hand-written SQL per provider. EF
  Core is the abstraction; adding a second one would be the actual mistake.
- **Not** regenerating the SQL Server migration history (§4).
