# Changelog

All notable changes to Vigil365 are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The
version lives in exactly two places — the API's `<Version>` and the client's
`package.json` — kept in step by `scripts/set-version.ps1` and enforced in CI by
`scripts/check-version.ps1`.

## [Unreleased]

## [1.2.0] — not yet released (pending the release walkthrough, `docs/MSP_V12_PLAN.md` §6)

### Added
- **PostgreSQL support.** Vigil365 now runs on SQL Server *or* PostgreSQL 14+,
  selected by a new `Database:Provider` setting (`SqlServer`, the default, or
  `Postgres`). The connection string stays in `ConnectionStrings:DefaultConnection`.
  Existing installs need no config change. `docker-compose.postgres.yml` is the
  Postgres flavour of the one-command deployment. This is the foundation for the
  MSP edition, where SQL Express's 10 GB ceiling is reached within a few dozen
  client tenants — see `docs/MSP_EDITION_PLAN.md`.
- `scripts/check-migrations.ps1`, run in CI: fails the build if either engine's
  migration set has drifted from the model, since every model change now needs a
  migration per engine.
- Model-level provider tests (`DatabaseProviderTests`) covering provider
  selection, per-engine column/index spellings, `DateTimeOffset` →
  `timestamptz` mapping, and migration attribution — none need a live database.
- Real-database parity suite (`Relational/`): Testcontainers starts SQL Server
  and PostgreSQL and runs the same assertions on both — migrations from empty,
  identity keys, fixed-key singleton rows, the filtered unique index, unbounded
  text, `DateTimeOffset` precision, retention pruning, the size query, and seed
  idempotency. Skips without Docker; CI requires it.

- **Tenant isolation (MSP foundation).** Every row now belongs to a tenant
  (`ClientTenants` table; `TenantId` on tenant-scoped tables). Reads are confined
  to the current tenant by an EF global query filter and writes are stamped and
  guarded in `SaveChanges`; with no tenant selected, scoped data access fails
  closed. Single-tenant installs are migrated into a seeded default tenant
  automatically and behave exactly as before. Admins can pre-select a tenant with
  the `X-Vigil-Tenant` header. Background workers run once per active tenant with
  per-tenant failure isolation. Verified by an isolation suite on real SQL Server
  and PostgreSQL, and by source guards (every entity classified; no
  `IgnoreQueryFilters` outside the one seam; no `Find` on tenant data).
  Uniqueness of Graph alert/audit ids is now per tenant.
- **Per-tenant Graph credentials and onboarding API** (MSP). Each tenant may hold
  its own app-registration credentials (`PUT /api/tenants/{id}/credentials`, secret
  protected at rest, never returned). `GET …/consent-url` produces the admin-consent
  link for a client's Global Administrator; `POST …/test` verifies the connection,
  records the client's Entra tenant id and consent time, and rejects credentials
  that belong to another organisation. Install-wide credentials keep working for
  the tenant they belong to, so single-tenant installs are unaffected. The
  collector runs per tenant and records each tenant's last collection status.
- **MSP staff scoping and client UI.** Non-Admin users see only the client tenants
  assigned to them (User Management → Clients column). A header tenant switcher
  scopes the whole dashboard to one client; the new **Clients** section shows a
  worst-first rollup of every permitted client's open alerts and collection health,
  and gives Admins the roster plus a guided onboarding dialog (add client → store
  credentials → generate the admin-consent link → test the connection).
- **Per-client alerting (MSP).** Route each client's alerts to the MSP, the client, or
  both (`/api/notification-routing`), switch an install-wide policy off or change its
  threshold for one client (`…/tenant-override`), create client-only policies
  ("This client only" when drafting), and get a daily MSP digest email of every
  client's open alerts and collection health, worst first. SIEM API tokens can be
  restricted to one client or select one with `X-Vigil-Tenant`.
- **One-go client onboarding (MSP).** Add a client, then click *Sign in as global
  admin & consent* — a popup opens Microsoft admin consent, the client's Global
  Administrator approves once (which provisions the app in their tenant), and Vigil365
  catches the callback on a new anonymous `/consented` landing page, records the Entra
  tenant id + consent time, and auto-runs the connection test. The consent `state` is
  signed and time-boxed (`ConsentState`). Clients consent to one shared
  multi-tenant MSP app (the installer's MSP mode creates it, or `register-app.ps1
  -MultiTenant`), so per-client credentials are optional. The onboarding dialog
  checks that app registration first and says exactly what would make consent fail.
- **Edition mode.** `Edition:Mode` = `Single` (default) or `Msp`. Single-organisation
  installs show no MSP screens and cannot add a second client. Sign-in is pinned to the
  install's own Entra tenant, so a client's users can never sign in to the dashboard.
- **MSP "Choose a client" screen** instead of errors when several clients are visible
  and none is chosen; the active client is named in the header, every toast and every
  CSV export filename.
- **One Graph permission list** (`graph-permissions.json`) shared by the installer, the
  API and `register-app.ps1`, with `docs/graph-permissions.md` generated from it. A
  copy-link fallback remains for admins who can't sign in in the popup.
- **MSP hardening.** Certificate authentication per client; parallel, staggered
  collection with per-client exponential backoff; `/health` database-size headroom
  warning (SQL Express ceiling); white-label brand name and colour on each client's
  reports; per-client routing and policy-override forms in the UI; and
  `docs/MSP_DATA_PROCESSING.md` for DPA reviews.
- **Installer: MSP mode and PostgreSQL.** Setup asks Single organisation or MSP. MSP
  mode makes the app registration multi-tenant with the `/consented` redirect, grants
  `Application.Read.All` in the MSP's own tenant for the readiness check, and needs a
  full SQL Server edition or PostgreSQL (it refuses SQL Server Express, with the
  reason). Re-running Setup over a Single install and choosing MSP converts it: same
  app registration, existing data becomes the first client. `deploy.ps1` /
  `enterprise-install` take `-Mode` and `-DatabaseProvider`.
- **Cross-client alert queue.** The Clients page lists open alerts across every client
  you may see, worst first, with a client filter. Acknowledge or resolve in place (the
  message names the client), or open an alert to switch to its client.
- **API tokens screen.** User Management → API tokens: create (shown once), list and
  revoke SIEM tokens; in MSP mode restrict a token to one client.
- **Database size warning** banner for Admins when `/health` reports `sizeWarning`.
- **MSP audit log** is one list across clients for MSP Admins, with a Client column.
- **Onboarding error states:** popup blocked, consent declined (Microsoft's error),
  window closed early and timeout are each explained inline in the dialog.
- **Tests:** component tests (React Testing Library), signed-in Playwright journeys
  through a test-only auth seam (CI asserts it is absent from the release bundle),
  and axe accessibility checks over the rendered MSP screens.

### Changed
- Switching client keeps the page you are on (in-page filters still reset).
- Non-Admins see per-client routing and policy overrides read-only, and Viewers no
  longer see policy Edit/Delete buttons the server would refuse.
- Muted text colours darkened to meet WCAG AA contrast; every settings, policy and
  onboarding field now has a programmatic label.
- The in-app "register the MSP app" endpoint was removed; the installer owns app
  registration, and the Clients page shows an MSP app readiness check instead.
- **Upgrade note:** 1.2 database migrations are one-way. Take a backup first; going
  back to 1.1 means restoring it (see `docs/OPERATIONS_RUNBOOK.md`).
- The pre-migration legacy-schema rescue in startup now runs only on SQL Server,
  which is the only engine that can have such a database.
- The Metrics tab's database-size figure is queried per engine
  (`sys.database_files` / `pg_database_size`).

### Fixed
- Audit-log hash chain failed verification on PostgreSQL (`timestamptz` keeps
  microseconds, .NET keeps 100 ns ticks); timestamps are truncated at write.
- Toasts and confirm dialogs were invisible on the Choose-a-client screen.
- A user whose only client was picked by the server never had it recorded, so
  per-client controls stayed hidden.
- A failed client rollup showed "No clients yet" instead of an error; the client
  routing card rendered nothing on failure.
- The onboarding consent poll kept running after the dialog closed.

### Security (pre-release QA review)
A senior-QA review of the MSP edition found and fixed, each with a regression test:
- **Forgeable consent state.** `/consented` accepted a hand-made, unsigned `state`,
  so anyone could mark a client as consented and stop its collection. The state is
  now signed with its own Data Protection purpose, expires after 30 minutes, works
  once, and `tenant` must be a tenant id that no other client already has.
- **Wrong tenant's data under a client.** A client added without an Entra id fell
  back to the install's credentials — the MSP's own tenant — so the MSP's data was
  collected, shown and alerted on as that client's. Such a client is now not
  connected until its admin consents.
- **One-go onboarding never collected.** A client consented to the shared MSP app had
  its credentials blanked; it now authenticates with the shared app in its own tenant.
- **Scheduled reports leaked across clients.** Schedules had no client and were sent
  with the first tenant's digest. In MSP mode they now belong to the selected client.
- Unassigned staff got the only active client by default; Analysts could change
  install-wide policies in MSP mode; a client's Teams webhook URL was returned to
  Analysts; a client-restricted SIEM token kept working after its client was
  deactivated; purging a client deleted its audit entries and broke the hash chain;
  audit entries took the selected client instead of the one acted on.

### Fixed (pre-release QA review)
- Backend: a client whose Graph access was fully broken was recorded as healthy;
  collected alerts that left Graph's feed never resolved; alerts stayed open after
  their policy was raised, disabled or deleted; per-policy and per-client notify
  addresses were ignored; client-only routing emailed the MSP; concurrent
  evaluations raised duplicates; entity suppression muted whole alerts; retention
  broke the audit chain; the cross-client queue dropped older critical alerts; the
  digest frequency was never saved; the tenant list counted auto-resolved alerts as
  open; parallel collections raced on the metrics counters.
- Client: a stale client selection locked the user out; deactivating the selected
  client showed the next client's data under the old name; failed loads looked like
  "nothing configured" or "all clear" (and Save could wipe real notification
  settings); "Open in client" and "Investigate" links did not open the alert;
  acting on a stale queue row reopened resolved alerts; re-activating a client
  erased its branding; the threshold override saved on every keystroke; controls
  were shown to roles the server refuses; toasts named the wrong client.
- Installer and deployment: the Docker image did not build; the service could not
  create its log folder; the Graph secret was written to a world-readable file;
  re-running Setup dropped operator settings and could adopt another server's app
  registration; `enterprise-install.sh`/`.ps1` produced services that could not
  start; `register-app.ps1` reported success on failure; docs contradicted the code.

### Upgrade notes
- `X-Forwarded-For` is trusted only from loopback or from
  `ForwardedHeaders:KnownProxies`/`KnownNetworks`; list a proxy in another
  container or host there.
- The audit CSV export has two new trailing columns, `TenantId` and `HashVersion`.
  New entries use hash version 1, which also covers `TenantId`; old entries still verify.
- In MSP mode, report schedules with no client are no longer sent; recreate them
  with a client selected.
- In MSP mode only Admins may create, edit, delete or import install-wide alert
  policies (Analysts get 403).
- The first collection after the upgrade resolves collected alerts that have
  dropped out of Graph's feeds, so open counts may fall. Open alerts whose policy is
  disabled, switched off for the client or deleted now auto-resolve.
- An entity-pattern suppression rule hides an alert only when every affected entity
  is covered.
- A policy's Notify Email, or a client override address, now receives that policy's
  alerts in place of the default recipient.
- Logs fall back to `%ProgramData%\Vigil365\logs` (or stdout only) when the
  configured folder is not writable.
- Consent links issued before the upgrade must be regenerated. One Microsoft tenant
  can belong to only one client (409 on a duplicate).
- `/health` `checks.collection` has a new field, `staleAfterMinutes`.
- `POST /api/api-tokens` rejects an expiry in the past (400).
- The Setup wizard asks before sharing an existing "Vigil365" app registration its
  configuration does not name (default: create "Vigil365 (<server>)").
- A production client build always drops the test-only sign-in bypass.

## [1.1.0] — 2026-08-28

A design refresh, two new real-data features, and an important installer fix.
Everything shown is measured or collected — no fabricated values.

### Added

- **Baseline & drift** (Rules & Notifications → Baseline). Capture the tenant's
  posture at a point in time from a real collection snapshot, then track drift of
  the latest snapshot against it (Secure Score, MFA coverage, risky users,
  non-compliant devices, alert counts, compliance issues). Admin-only capture,
  audited. Replaces the former Coverage Scorecard tab.
- **Real system metrics** (Rules & Notifications → Metrics). Collector uptime,
  Graph calls per run, evaluation-latency p95, and live database size, with a
  Prometheus-style metrics table and a Graph-throttling trend. All values are
  measured: Graph requests and 429s are counted in the Graph client and persisted
  per run; evaluation timing is measured per cycle. Cumulative `_total` counters
  are persisted and survive a service restart. New `GET /api/metrics`.

### Changed

- **UI restyle** to the Vigil365 design system across every page — icon-less,
  tone-bordered KPI cards; tightened card frame and typography; underline section
  sub-tabs; a segmented pill control for the Rules & Notifications inner tabs, now
  with stat-card summaries on the Policies, Suppressions and Collection-runs tabs
  and applied-status on Templates. No behavioural change.

### Fixed

- **Remote SQL Server configuration.** The installer forced Windows authentication
  when preparing the database, discarding any SQL username/password entered for a
  remote server and failing to create a local-service login on a host that wasn't
  the SQL server — so remote-SQL installs always failed. Database preparation is
  now authentication-aware: SQL-authentication connection strings are honoured, the
  service connects with those credentials, and the Windows login is created only
  for local Trusted_Connection installs.

### Removed

- The Privacy / Demo Mode blur toggle.

## [1.0.0] — 2026-08-07

First public release of Vigil365: a self-hosted, **read-only** Microsoft 365
security monitoring dashboard. It collects from Microsoft Graph on a schedule,
evaluates metric / activity / anomaly alert policies, notifies over
Teams / email / webhook, and reports through trends, compliance assessment and an
executive digest — with in-app RBAC over a tamper-evident audit trail. It reports
and recommends, and never changes anything in the tenant.

Distributed as a single self-contained Windows installer (`Vigil365-Setup.exe`)
that carries the application, the web UI and the .NET runtime — the target server
needs no source tree, Node.js or .NET.

### Installation

- **Self-contained setup wizard** — one `Vigil365-Setup.exe` (~120 MB), built at
  release time by `scripts/build-installer.ps1`. It checks prerequisites,
  registers the Entra application, prepares the database, sets up HTTPS and
  installs an auto-starting Windows service. The only external tool is Azure CLI,
  used solely for the Entra registration, and installed automatically if missing.
- **Deployment scope choice** — "Just this computer" binds loopback with no
  certificate (Entra permits `http://localhost` redirect URIs), or "Other people
  on our network" takes a certificate from the Windows store, a `.pfx`, or a
  generated self-signed one. `scripts/request-cert.ps1` obtains a real Let's
  Encrypt certificate for an internet-reachable host.
- **Automatic Entra provisioning** — the wizard registers the app in the
  administrator's own tenant (resolved from their email via OpenID discovery,
  not whatever the CLI happened to be signed into), grants the fourteen required
  Graph permissions, grants admin consent, and creates the collector's client
  secret — so collection works on first run with nothing to configure in the
  portal.

### Security

- CSV exports are guarded against spreadsheet formula injection. Alert titles,
  display names and audit actors are tenant-controlled, so a value beginning
  `=`, `+`, `-` or `@` would execute on open in Excel or Sheets. Applied to all
  three exporters.
- Idle (30 min) and absolute (12 h) session timeouts. Idle counts real user
  input only — the app's own polling is not evidence anyone is present — and the
  session start is held in `sessionStorage` so a refresh cannot reset the cap.
- API tokens for SIEM access: 32 CSPRNG bytes, stored only as a SHA-256 hash
  with a short display prefix, plus scopes, expiry, revocation and last-used.
  The raw token is shown exactly once, at creation.
- Outbound webhooks are signed Stripe-style — HMAC-SHA256 over
  `{timestamp}.{body}`, with the timestamp sent alongside so receivers can
  reject replays. The signing secret is encrypted at rest.
- Unknown `/api/*` paths now return `404` JSON instead of `200` HTML from the
  SPA fallback, which previously masked broken clients and confused scanners.
- Removed `react-router-dom`. It carried a high-severity advisory
  (GHSA-qwww-vcr4-c8h2) and was never imported — the app has its own hash
  router. With a `postcss` fix this took the project from three high-severity
  advisories to zero.
- CI now fails on vulnerable NuGet or npm packages, and the push trigger was
  corrected — it listed only `main`, so push-triggered CI had never run.
- Patched four High-severity transitive advisories surfaced once the full
  solution audit ran — `System.Security.Cryptography.Xml`, `System.Formats.Asn1`,
  `System.Net.Http` and `System.Text.RegularExpressions` — pinned to fixed
  versions across the API, tests and installer.
- CI gained a Windows job that compiles the WPF installer, so a break there fails
  the build instead of surfacing only at release time.

### Added

- **Standing suppression rules** — silence known-noisy alert classes at source
  rather than acknowledging them repeatedly. Mutations are Admin-only and
  audited, because suppressing an alert class is a security decision.
- **Policy dry-run** — replay a policy against stored history before saving it
  ("would have fired 3 times in 30 days"). Counts *episodes*, not evaluation
  cycles, because the evaluator keeps one open alert per policy; and reports
  honestly when history cannot answer rather than returning a misleading zero.
- **Alert-ops metrics** — MTTA, MTTR, resolution rate and per-analyst workload,
  computed from timestamps the workflow already recorded.
- **Policy export/import** as portable JSON packs. Runtime state never travels,
  and notification recipients are stripped by default since packs get shared.
- **Executive digest as PDF**, alongside the existing HTML email and CSV.
- Digest entries now carry each alert's **category, status and assignee** in both
  the HTML and CSV, so a digest can be triaged without opening the app.
- **SIEM export** — `/api/siem/alerts` and `/api/siem/health`, authenticated by
  scoped API token.
- **First-run setup checklist** and a live **Graph permissions reference**
  showing granted/missing status per permission, inferred from the last run.
- **Contextual per-page help** describing what each page shows.
- **Entity investigation** is now reachable from an alert, not only from the
  Ctrl+K palette.
- **Compact density toggle** and a formal ten-step type scale.
- Frontend test suite (vitest) and a post-deploy smoke test
  (`scripts/smoke-test.ps1`) that verifies a running instance end to end.

### Changed

- Graph failures are translated into instructions. A denied collector source
  used to render as raw JSON; it now names the exact permission to grant and
  where.
- `Program.cs` split from 2,545 lines into nine per-domain endpoint modules,
  leaving 380 lines of host, DI and middleware. Verified by diffing the full
  90-endpoint route table, including the authorization on every endpoint.
- Every clickable row is keyboard-accessible, with a skip link and a `<main>`
  landmark. Previously the app was mouse-only for its core action — opening an
  alert.
- Dashboard panels now distinguish "failed to load this cycle" from "not
  configured", instead of telling users to run a collection that had already
  succeeded.
- The version shown in the UI is injected from `package.json` at build time
  rather than hardcoded, so it cannot claim a version the build is not.
- README corrected against what the app actually does — it had promised a
  geographic sign-in map that does not exist, described server-side alerts as
  browser storage, claimed every Graph permission was read-only when attack
  simulation requires `ReadWrite.All`, and listed several endpoints that had
  been renamed or removed.

### Fixed

- Tenant Activity rendered twice (duplicate conditional), causing a double fetch.
- "Tampering detected" — the most serious signal the product emits — displayed
  as a green success toast.
- Overview's total and the alert queue disagreed once a tenant passed 200 open
  alerts; the queue now states what it is showing.
- Dashboard fetch failures were swallowed while the header still stamped a fresh
  "Updated" time over stale cards.
- The collection banner's "Details" link opened Microsoft's service advisories,
  which cannot explain a Vigil365 collector failure; it now opens Collection
  Runs, where the per-source error is readable.
- Error states offered no retry, and relative timestamps froze at render.

### Fixed — installer and first run

Each of these previously produced an install that reported success and did not
work:

- Registered the app in the wrong tenant (whichever the CLI was signed into)
  rather than the administrator's own; now resolved and verified.
- Windows service was never created — `sc` was invoked through `cmd.exe`, which
  split the quoted binary path; now invoked directly with the exit code checked
  and startup confirmed to reach RUNNING.
- The service account had no SQL login (SQL Express grants sysadmin only to local
  administrators), so it could never connect; the login and database are now
  created during install.
- DataProtection keys were written under `Program Files`, unwritable by the
  service, so the keyring never persisted; moved to `ProgramData` with an ACL.
- A fresh database crashed on first start — `NotificationSettings` / `GraphConfig`
  are single-row tables with a fixed key, but the migration made those keys
  identity columns; corrected with a migration.
- Re-running the wizard failed to reconfigure an existing app registration
  (`CannotDeleteOrUpdateEnabledEntitlement`); the exposed scope is now left
  untouched when it already exists.
- Certificate-thumbprint auth threw a cryptic error on Linux (the Docker path)
  instead of a clear message when a certificate store could not be opened.
- Sign-in dead-ended with `interaction_in_progress` after an abandoned redirect;
  the stale MSAL state is now cleared and retried.
- The Setup page threw `EmptyState is not defined` because the component was used
  without importing it — shipped because the build does not type-check.
