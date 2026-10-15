# Vigil365 — MSP Edition (Multi-Tenant + Multi-Database)

**Status:** all eight phases implemented (branch `feat/msp-phase1-db-providers`).
Remaining: first live Postgres run (CI), a browser walkthrough of the MSP UI, review and merge. Supersedes and absorbs `MSP_MULTITENANT_PLAN.md` and
`DATABASE_PROVIDERS_PLAN.md`; keep those for the reasoning behind individual decisions,
build from this one.

**What this is:** Edition 2 of Vigil365 — a multi-tenant build an MSP runs once to watch
many client Microsoft 365 tenants, on either SQL Server or PostgreSQL.

**What it is not:** a replacement for Edition 1. The single-tenant in-tenant build
("your data stays put") and the MSP build (MSP-custodied data) are *different trust
models sold to different buyers*. Both ship. Both stay read-only — no remediation.

---

## Part 0 — The two constraints that shape everything

**1. Cross-tenant data leakage is the only unrecoverable failure.**
Every other bug is an inconvenience; showing Client A's alerts to Client B ends the
product. Every design choice below is subordinate to this. It is the reason the
database work comes first, the reason the query filter is enforced rather than
conventional, and the reason the test harness is a prerequisite rather than a follow-up.

**2. In-memory tests cannot verify isolation.**
The suite currently uses `UseInMemoryDatabase` at all four sites. In-memory is not a
relational provider — it does not generate SQL, so an EF global query filter "passing"
there proves nothing about what a real database returns. Until a real-provider harness
exists, there is no way to earn confidence in constraint 1.

These two facts set the ordering of the entire plan.

---

## Part 1 — Database provider abstraction

Done first, because the isolation tests in Part 2 must run on every provider that ships,
and validating them twice is the waste that doing this later guarantees.

### 1.1 Current provider surface (audited)

Small and contained — it has not leaked into the query layer.

| Site | What | Action |
|---|---|---|
| `Program.cs:72` | the one `UseSqlServer(...)` | becomes a provider switch |
| `Program.cs:155-178` | legacy-DB probe (`OBJECT_ID`) + `__EFMigrationsHistory` baselining | gate to SQL Server, do not port |
| `Data/AlertingSchema.cs` (286 ln) | idempotent T-SQL DDL for pre-migration installs | gate to SQL Server, do not port |
| `MetricsService.cs:107-124` | DB size via `sys.database_files` | provider adapter |
| `AppDbContext.cs` x3 | `HasColumnType("nvarchar(max)")` | drop; use provider default |
| `Data/Migrations/*` x15 | SQL Server DDL | parallel per-provider sets |

Two audit findings that de-risk this materially:

- **All 35 temporal properties are `DateTimeOffset`; there are zero bare `DateTime`
  properties.** The usual Postgres-port killer — `DateTime.Kind` ambiguity, `timestamp`
  vs `timestamptz` — does not apply. Npgsql maps `DateTimeOffset` to `timestamptz`
  cleanly.
- **`AlertingSchema` and the legacy probe never need porting.** They rescue
  *pre-migration SQL Server* databases. A Postgres install is always fresh; there is no
  legacy Postgres estate and there never will be. Gating them is correct, not a
  shortcut — a port would be unreachable code.

### 1.2 Configuration

```json
"Database": { "Provider": "SqlServer" },
"ConnectionStrings": { "DefaultConnection": "..." }
```

`Provider` defaults to `SqlServer`, so **every existing Edition 1 install keeps working
with no config change**. Selection happens in exactly one place.

### 1.3 The adapter seam

One `IDatabaseProviderAdapter`, one implementation per provider, exactly two members:

1. `Task<long?> GetDatabaseSizeBytesAsync(ct)` — `sys.database_files` on SQL Server,
   `pg_database_size(current_database())` on Postgres. Feeds the size-headroom warning
   in §1.6.
2. Large-text column type — preferred fix is to delete the three `HasColumnType` calls
   and let each provider's unbounded-`string` default apply (`nvarchar(max)` / `text`).
   Use the adapter only if a migration diff shows the default is wrong.

**Hold this interface at two members.** A third seam is a signal that provider-specific
logic is leaking back into the query layer, and should be pushed back into LINQ instead.

### 1.4 Migrations

EF Core cannot share a migration set across providers. **As built:** the two sets
live in one assembly, told apart by context type — the 15 SQL Server migrations stay
in `Data/Migrations/` attributed to `AppDbContext` (untouched, so no install's
`__EFMigrationsHistory` moves), and Postgres migrations live in
`Data/Migrations/Postgres/` attributed to `PostgresAppDbContext`, a behaviour-free
subclass that exists only so EF resolves the right set. DI registers the subclass as
the implementation behind `AppDbContext` when the provider is Postgres; no consumer
sees it. This avoided a separate-assembly split (which would have been circular, as
`Api` is both the context project and the startup project) and avoided moving the
SQL Server files. A test asserts every migration is attributed to exactly one engine
and that the two contexts resolve disjoint sets.

- **SQL Server keeps its existing 15 migrations byte-for-byte.** Do not regenerate:
  existing installs have those `MigrationId`s in `__EFMigrationsHistory`, and the
  baseline is pinned by the legacy-rescue path.
- **Postgres gets one squashed `InitialCreate`** from the current model. No history to
  preserve, so replaying 15 historical migrations is pure cost.
- Every model change from now on generates **two** migrations.

**CI must fail on drift between the two sets.** This is the permanent tax of the
decision; automate it on day one, because it will not survive on discipline. Practically:
a job that builds the model, generates a migration against both providers, and fails if
either produces a non-empty diff.

### 1.5 Provider parity test harness

**As built:** the in-memory tests were kept, not replaced — they are fast, need no
Docker, and are right for business logic. Alongside them, `Tests/Relational/` is a
Testcontainers harness (`RelationalEngines` collection fixture) that starts one real
SQL Server and one real Postgres per run and hands out a freshly-migrated, uniquely
named database per test. `RelationalParityTests` is a theory over both engines. Without
Docker the suite skips with the reason; CI sets `VIGIL365_REQUIRE_DB_TESTS=1` so it
fails instead. The Phase 4 isolation suite (§2.4) is written against this fixture.

This is a prerequisite for Part 2, not a nicety — see Part 0, constraint 2.

Minimum gates: migrations apply cleanly from empty on both; the full existing suite
passes on both; the isolation suite (§2.4) passes on both.

### 1.6 Sizing and the Express question

SQL Express caps at 10 GB/database and a 1.4 GB buffer pool. `AuditEvents` (directory
audits, 90-day retention) dominates growth at roughly 200–500 MB per client tenant —
*an estimate from row shape, to be replaced with a real `sp_spaceused` figure*. That is
a wall at ~20–40 small tenants, and the buffer-pool cap bites earlier than the size cap
because MSP rollup views scan across all tenants at once.

Therefore: **Express is not a supported MSP target.** Document Postgres or SQL Server
Standard / Azure SQL for MSP; keep Express for Edition 1 and MSP lab use only. Surface a
database-size headroom warning on the admin health page so an MSP does not discover the
ceiling at 9.9 GB when writes begin failing.

---

## Part 2 — Tenant isolation (the foundation)

No UI in this part. It ends when isolation is proven, on both providers.

**As built (Phase 4).** Everything below is implemented; deviations from the
original text are called out inline.

- `ClientTenant` is keyed by a **Guid**, not an identity int, so the migration can
  reference the well-known default tenant (`ClientTenant.DefaultId =
  00000000-…-0001`) as a column default on both engines without identity-insert or
  sequence games. The tenancy migration seeds that row (`HasData`) and backfills
  every pre-existing scoped row into it — a single-tenant install upgrades with no
  operator action and keeps working exactly as before.
- Classification is code (`TenantClassification`) *and* interfaces on the entities
  (`ITenantScoped` / `ITenantOptional`). A test fails if they disagree, if an entity
  is unclassified, or if `IgnoreQueryFilters` appears anywhere but
  `AppDbContext.CrossTenant<T>()`.
- Filters are built generically from the interfaces in `AppDbContext`; writes are
  enforced in a `SaveChanges` override: scoped inserts are stamped, a foreign
  `TenantId` on insert/update/delete is refused, deleting by an unloaded stub is
  refused (it would delete a foreign row blind), and no tenant means an exception —
  never "all rows".
- `DbSet.Find`/`FindAsync` bypass global filters; the 11 call sites were replaced and
  a source-scanning test forbids new ones.
- **Unique indexes gained `TenantId`** (`SecurityAlerts`, `AuditEvents`) — two clients
  legitimately report the same Graph ids. The original plan did not call this out.
- `TenantBaseline` lost its fixed `Id = 1` and is keyed by `TenantId` (one per tenant).
- Request resolution: `TenantResolutionMiddleware` honours `X-Vigil-Tenant` (Admin
  only until Phase 6 assigns tenants to staff) or, with no header, the sole active
  tenant. Workers go through `TenantIterator` — one scope per active tenant,
  per-tenant failure isolation.
- **Phase 5 bridge:** until credentials are per tenant, `GraphCollectionWorker`
  collects only into the tenant whose recorded Entra id matches the configured
  credentials (or has none recorded), so a second tenant can never be filled with
  another organisation's data.

### 2.1 The `ClientTenant` entity

New root entity: MSP-assigned name, Microsoft tenant id, onboarding/consent state,
connection health, per-tenant Graph credentials, retention overrides, active/suspended.

`GraphConfig` — today a single row (`Id = 1`) holding one tenant's credentials — is
**superseded by per-tenant credential storage on `ClientTenant`**. Edition 1 keeps
`GraphConfig`; the MSP build migrates that single row into the first `ClientTenant` on
upgrade, which doubles as the "convert my single-tenant install to MSP" path.

### 2.2 Entity classification

Every one of the 17 `DbSet`s must be explicitly classified. Nothing may be left
unclassified — an unclassified entity is an isolation hole.

**Tenant-scoped** (`TenantId` required, global query filter applies):
`SecurityAlerts`, `CollectionRuns`, `TriggeredAlerts`, `NotificationLogs`,
`TrendSnapshots`, `AlertNotes`, `SuppressionRules`, `AuditEvents`, `TenantBaselines`

**MSP-global** (no `TenantId`; belongs to the MSP, not a client):
`AppUsers` (MSP staff), `ApiTokens`

**Dual — MSP-level default with optional per-tenant override** (`TenantId` nullable,
null = the MSP-wide default):
`AlertPolicies`, `NotificationSettings`, `ReportSchedules`, `MetricsCounters`

**Special:**
- `AuditEntries` — nullable `TenantId`; MSP-level actions record null, client-scoped
  actions record the tenant. **The SHA-256 hash chain stays MSP-global and unbroken** —
  do not chain per tenant, or a suspended tenant's pruned rows break verification.
- `GraphConfig` — removed in MSP; see §2.1.

The dual category is where isolation bugs will actually live. A nullable `TenantId`
means the filter must express *"this tenant's row, or the MSP default when no
per-tenant row exists"* — which is a fallback, not a filter. Write those queries once,
in one place, and test both branches.

### 2.3 Enforcing the filter

EF Core global query filter on every tenant-scoped entity, driven by an ambient
`ITenantContext`.

**The hard part is that there are two callers with different shapes:**

- **Request scope** — tenant resolved from the authenticated staff user's selection plus
  their permitted tenant set. Straightforward.
- **Background workers** — `GraphCollectionWorker`, `NotificationDigestWorker`,
  `ReportScheduleWorker`, `DataRetentionWorker` run with no HTTP user and no ambient
  tenant. Each must iterate tenants and open a **scope per tenant** with the context
  set explicitly.

A worker that resolves a null tenant must **fail closed — throw, never fall through to
unfiltered**. The default for "no tenant set" is an error, not "all rows". This single
rule prevents the most likely class of leak.

Deliberate cross-tenant reads (MSP rollup views, the client roster) go through one
explicit, small, individually-tested `IgnoreQueryFilters()` surface. Not scattered
call sites — one seam, so it can be reviewed as a unit.

### 2.4 Isolation test suite

The deliverable of Part 2, run against real SQL Server and real Postgres —
`Tests/Relational/TenantIsolationTests` (12 scenarios × 2 engines) plus
`TenantClassificationTests` (model/source guards, no database):

- Seed 3+ tenants with overlapping data; assert every tenant-scoped `DbSet` returns only
  the active tenant's rows.
- Assert writes cannot set a foreign `TenantId`.
- Assert a null/unset tenant context throws rather than returning all rows.
- Assert each background worker processes exactly one tenant per scope.
- Assert the dual-category fallback returns the override when present and the MSP
  default when absent — both branches.
- Assert the audit hash chain still verifies with tenant-scoped rows interleaved.
- A guard test that **fails when a new `DbSet` is added without a classification**, so
  §2.2 cannot silently rot.

---

## Part 3 — Connection and collection

**One-go onboarding (added after Phase 8).** The four manual steps collapsed to two:
add the client, then *Sign in as global admin & consent*. A popup opens Microsoft
admin consent for the client tenant; approval provisions the multi-tenant app's
service principal in that tenant (so "create app + consent" is that one approval) and
redirects to an anonymous `/consented` landing page. The landing trusts only a signed,
30-min `state` (`ConsentState`, HMAC via SecretProtector) naming the ClientTenant row;
it records the Entra tenant id + consent time and resets backoff. The onboarding dialog
polls the tenant's status while the popup is open, then auto-runs the test. The shared
multi-tenant MSP app was first created on demand by an in-app endpoint that shelled out to
Azure CLI; that endpoint was removed in v1.2 Stage 2 (it could not work on installed copies)
in favour of a read-only readiness check — see `MSP_V12_PLAN.md` M2. Per-client credentials
are optional. Popup-blocked / can't-sign-in-here falls back to a
copyable consent link + manual test.


**As built (Phase 5).**

- **Per-tenant credentials live on `ClientTenant`** (`ClientId`, protected
  `ClientSecret`, optional sovereign `LoginInstance`/`BaseUrl`) plus connection health
  (`ConsentGrantedAt`, `LastCollectionAt`, `LastCollectionStatus`, `LastError`).
  `GraphConfig` stays as the *install-wide* credential store for single-tenant
  installs — it was not removed, because the resolution rule below makes it the
  natural fallback rather than something to migrate away from.
- **`TenantGraphCredentials`** (scoped) is the one place that decides whose
  credentials a Graph call uses: the tenant's own if set; else the install-wide ones
  *only if* the tenant has no recorded Entra id or its Entra id is the one those
  credentials belong to; else unconfigured. Non-credential settings (interval,
  lookbacks, feed paths) are always install-wide. Eight unit tests pin the rules.
- `GraphApiClient` resolves credentials lazily from that service; every dashboard
  endpoint's "is Graph configured?" check became per tenant (24 sites) with no other
  caller changes. `GraphCollectionWorker` drops the Phase-4 bridge: it collects every
  active tenant that resolves credentials and records health on the tenant row, with
  per-tenant failure isolation from `TenantIterator`.
- **Onboarding API** (`/api/tenants`, Admin, audited): create/update/deactivate/purge,
  set/clear credentials (secret never returned), **consent-url** (admin-consent link
  for the multi-tenant app registration, `organizations` authority until the Entra id
  is known) and **test** (calls `/organization` as that tenant, records the Entra id
  and consent time, refuses credentials that belong to a different Entra tenant).
  Purge cascades — the isolation suite's offboarding test is what proves it.
- Not done here, deliberately: the onboarding *wizard UI* (Phase 6, with the tenant
  switcher) and certificate auth per tenant (Phase 8, §3.2 — the secret path is
  the MVP; the cert path exists install-wide already and slots into the same seam).

### 3.1 Microsoft-side

Multi-tenant app registration; per-client admin consent via GDAP. Onboarding wizard:
generate consent URL, client admin consents, verify by calling Graph, record tenant id
and health. Surface consent/role expiry before it breaks collection, not after.

### 3.2 Credentials

**Certificate auth for the MSP app, not a shared secret** — the blast radius of one
credential now spans every client. Vault-stored, rotatable. This pulls the cert-auth
backlog item forward; it is a precondition of MSP, not a hardening nicety.
`SecretProtector` extends to per-tenant credential encryption.

### 3.3 Collector

`GraphCollectionWorker` becomes a per-tenant loop: staggered scheduling, a parallelism
cap, and **per-tenant failure isolation and backoff** — one client's expired consent or
throttling must not stall collection for the other forty. Per-tenant Graph throttle
budgets tracked separately. Every collected row is tagged with its `TenantId` at write.

---

## Part 4 — Access control and UI

**As built (Phase 6).**

- **Staff scoping** is a `UserTenantAssignments` table (global entity) and one rule,
  `TenantAccess`: Admins see every active tenant; everyone else exactly the active
  tenants assigned to them; an unassigned non-Admin in a multi-tenant install sees no
  client. The request middleware now validates `X-Vigil-Tenant` against that rule for
  any signed-in user (the Admin-only restriction is lifted) and auto-selects a user's
  only permitted tenant. Single-tenant installs are unaffected: the sole tenant applies
  to everyone and the assignment column never appears.
- **API:** `GET /api/tenants/me` (switcher data), `GET /api/tenants/rollup` (per
  permitted tenant: open triggered alerts by severity, unresolved critical/high
  security alerts, collection health — via the `CrossTenant` seam), and Admin
  `GET/PUT /api/tenants/assignments[/{email}]`.
- **UI:** a header **tenant switcher** (hidden with fewer than two permitted tenants;
  selection persists in `localStorage` and is sent as `X-Vigil-Tenant` on every call;
  changing it reloads the app so no page keeps another client's data); a **Clients**
  section with the worst-first **rollup** cards and, for Admins, the **roster** and a
  four-step **onboarding dialog** (client → credentials → consent link → test); and a
  **Clients** column on User Management with a per-user assignment picker.
- Not done: white-label per-client reporting (Phase 8 with the DPA/reporting work).

### 4.1 MSP staff model

Existing roles (Admin/Analyst/Viewer) gain a tenant dimension:

- **MSP-Admin** — all tenants, onboarding, staff management.
- **Tenant-scoped staff** — Analyst/Viewer against an explicit assigned tenant set.
- Optional per-client read-only access, if you want to sell clients a view of their own
  data.

Enforced at the `ITenantContext` layer (§2.3), so it is one mechanism with authorization,
not a second parallel one.

### 4.2 UI

Tenant switcher; client roster with health and alert counts; per-client views reusing
Edition 1's pages under a tenant scope; cross-tenant rollup (worst-posture-first, the
MSP's actual morning triage view); white-label per-client reporting.

The rollup views are the ones that read across tenants — they use the single
`IgnoreQueryFilters()` seam from §2.3 and deserve disproportionate review.

---

## Part 5 — Alerting

**As built (Phase 7).**

- **Per-client routing** is a small tenant-scoped row, `TenantNotificationRouting`,
  layered over the install-wide `NotificationSettings` at dispatch time by the pure
  `NotificationRouting.Apply` rule: alerts go to the MSP, the client, or both; the
  client's own Teams/webhook win when set; an optional per-client minimum severity.
  The install-wide row stays the single place SMTP is configured, so
  `NotificationSettings` did not need per-tenant copies (and its fixed `Id = 1` key
  stays). A tenant with no routing row behaves exactly as before. Per-tenant digest
  and failure-alert timestamps moved onto the routing row — one shared timestamp
  would have let the first tenant's digest suppress every other tenant's in the same
  hourly pass. API: `GET/PUT /api/notification-routing`.
- **Policy overrides**: `AlertPolicyTenantOverride` (tenant-scoped, keyed by
  tenant+policy) switches an MSP-wide default off for one client or changes its
  threshold / notify address. The evaluator applies overrides to detached copies so
  nothing leaks into the shared row, while trigger statistics still land on the
  tracked original. Client-specific policies are ordinary policy rows created with
  `?scope=tenant` (UI: "This client only" when drafting a policy with a client
  selected). API: `GET /api/alert-policies/tenant-overrides`,
  `PUT/DELETE /api/alert-policies/{id}/tenant-override`.
- **MSP digest**: one email a day (install-wide `MspDigestEnabled` / `MspDigestHourUtc`)
  to the MSP's default recipient, every active client worst-first, built by
  `TenantRollupService` (now also behind `/api/tenants/rollup`). Runs in the digest
  worker's MSP-level pass, outside any tenant, and only with two or more active clients.
- **SIEM tokens**: an API token may be restricted to one client (`tenantId` on
  creation); such a token is pinned to that client and may not ask for another. An
  install-wide token may select a client with `X-Vigil-Tenant`, or rely on the
  sole-tenant fallback. Tenant resolution happens in the middleware before the
  endpoint's own scope check.
- Not done: a UI for routing/overrides beyond the policy scope toggle and badge —
  the endpoints exist; the notification-settings page gains those forms in Phase 8
  alongside white-label reporting.

Per-tenant policies and thresholds, with MSP-wide defaults inherited unless overridden
(the dual category, §2.2). Per-tenant notification routing — a client's alerts may go to
the MSP, the client, or both. Cross-tenant feed and a single MSP digest, so an MSP with
forty clients gets one useful morning email rather than forty.

---

## Part 6 — Security and compliance

**As built (Phase 8).**

- **Certificate auth per client**: `ClientTenant` carries a thumbprint or PFX path
  (+ protected password); the credentials API and onboarding dialog accept either a
  secret or a certificate; the certificate wins when both exist, mirroring the
  install-wide behaviour. The install's own certificate is never applied to another
  organisation. Rotation is per client via the same endpoint.
- **Scale tuning**: `TenantIterator` gained `maxParallel` and `stagger`;
  `Graph:TenantParallelism` (default 2) and `Graph:TenantStaggerSeconds` (default 3)
  drive collection; the collector's process-wide gate became a per-tenant gate.
  **Per-tenant backoff** (`CollectionBackoff`, pure and tested): interval × 2^failures,
  capped by `Graph:MaxBackoffMinutes` (default 240); a success, new credentials or
  re-activation reset it. The roster shows "Backing off until …".
- **Database headroom**: `/health` reports `database.sizeBytes` and flags
  `sizeWarning` (status *degraded*) above `Database:SizeWarningBytes` (default 8 GiB,
  chosen for SQL Express's 10 GB ceiling).
- **White-label reports**: `ClientTenant.BrandName` / `BrandAccentColor` replace
  "Vigil365" and the accent colour in the client's digest email, CSV and PDF, and
  in attachment filenames. Set in the onboarding dialog.
- **Routing and override forms** (deferred from Phase 7): "This client's routing" card
  on the notifications tab and a "This client" column on the policies table, both
  only when a client is selected. MSP digest toggle and hour on the delivery rules.
- **DPA documentation**: `docs/MSP_DATA_PROCESSING.md` — what is collected per
  client, isolation guarantees and how they are tested, who can see what, where data
  is sent, retention defaults, offboarding semantics, sub-processors, incident steps.
- Per-tenant audit trail was already delivered in Phase 4 (§2.2).

Raised stakes: the MSP is now a data processor for every client.

- Per-tenant audit trail (§2.2), MSP actions scoped and logged.
- GDAP least-privilege and time-bound access; surface expiry.
- Certificate auth + vault + rotation (§3.2).
- Consider per-tenant key separation for the strongest posture.
- DPA and data-handling documentation for the MSP-as-processor model.
- Data-deletion-on-offboarding: removing a client must provably remove their data, and
  the isolation harness is what proves it. Add an offboarding test.

---

## Part 7 — Sequencing

Each phase is shippable and ordered by dependency, not preference.

| Phase | Content | Gate to exit |
|---|---|---|
| **1** ✅ | Provider switch, gate legacy path, adapter seam (§1.1–1.3) | Both providers boot; Edition 1 unaffected |
| **2** ✅ | Per-provider migrations + CI drift check (§1.4) | Migrations apply clean from empty, both |
| **3** ✅ | Testcontainers harness (§1.5) | Existing suite green on both providers |
| **4** ✅ | `TenantId` + `ClientTenant` + query filter + isolation suite (§2) | **Isolation suite green on both providers** |
| **5** ✅ | Multi-tenant app reg, onboarding, per-tenant collection (§3) | A second client tenant collects independently |
| **6** ✅ | Staff scoping, tenant switcher, roster, rollups (§4) | — |
| **7** ✅ | Per-tenant alerting and routing (§5) | — |
| **8** ✅ | Cert auth, per-tenant audit, white-label, scale tuning, DPA (§6) | — |

Phase 4's gate is the one that matters. Do not begin Phase 5 with it amber.

---

## Part 8 — Effort and risk

**Effort:** a multi-week v2. Phases 1–3 are on the order of days, dominated by the
migration split and the test harness rather than the provider switch. Phase 4 is the
large, meticulous one — `TenantId` across 17 entities, every query, and four background
workers. Phases 5–8 are conventional feature work.

**Ongoing cost:** the dual-migration tax (§1.4), permanently. Automated on day one or it
will not hold.

**Risks:**

| Risk | Mitigation |
|---|---|
| Cross-tenant leakage | Global filter + fail-closed default + isolation suite on both providers; single audited `IgnoreQueryFilters` seam |
| Provider drift | CI diff check; full parity suite |
| Scale (tenants x collection frequency) | Stagger, parallelism cap, per-tenant backoff |
| Consent/GDAP churn | Connection-health surfacing, per-tenant graceful failure |
| Dual-category fallback bugs | One implementation, both branches tested |

**Non-goals:** remediation actions; raw-log SIEM ingestion; SQLite/MySQL now (the §1.3
seam keeps them cheap later); a repository layer over EF — EF *is* the abstraction, and a
second one would be the actual mistake; regenerating SQL Server migration history.
