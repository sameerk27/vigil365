# Vigil365

Microsoft 365 security alerting dashboard. ASP.NET Core 8 minimal API + React 19/Vite SPA,
SQL Server or PostgreSQL, single-tenant or MSP (many client tenants) edition.
Default branch: `master`. Repo: github.com/sameerk27/vigil365.

## Layout

- `src/M365SecurityDashboard.Api/` - API. `Program.cs` (DI, auth, CSP, middleware), `Endpoints/*Endpoints.cs`
  (one static `Map…Endpoints` per area), `Services/` (Graph collection, evaluators, workers;
  `Posture/`, `PowerPlatform/`), `Data/` (EF contexts + migrations for both engines), `Models/GraphOptions.cs` (config).
- `src/M365SecurityDashboard.Api.Tests/` - xUnit. `Relational/` runs on real SQL Server + Postgres in Docker.
- `src/m365-security-dashboard-client/` - SPA. `src/pages/*Page.tsx` (+ `.test.tsx`), `src/services/` (`api.ts` = MSAL +
  `apiFetch`), `src/components/`, `e2e/` (Playwright; `e2e/signed-in` uses stubbed APIs, no backend).
- `src/M365SecurityDashboard.GuiInstaller/` - WPF installer, Windows only; don't build it via the .sln on Linux.
- `docs/` - plans (`*_PLAN.md`), `THREAT_MODEL.md`, `graph-permissions.md`, public site (`index.html`, `llms.txt`).
- Root scripts: `install.ps1`, `enterprise-install.{ps1,sh}`, `register-app.ps1`; tests in `scripts/tests/`.
- Ignore: `bin/ obj/ dist/ publish*/ wwwroot/` (build output), `*.log`.

## Commands (run the narrowest one that proves the change)

```bash
dotnet test src/M365SecurityDashboard.Api.Tests --filter "FullyQualifiedName~PowerPlatform"   # one area
dotnet test src/M365SecurityDashboard.Api.Tests                                                # all (point at the project, not the .sln)
cd src/m365-security-dashboard-client && npx vitest run PowerPlatformPage                      # one client test file
cd src/m365-security-dashboard-client && npx tsc --noEmit                                       # type-check
cd src/m365-security-dashboard-client && npx playwright test e2e/signed-in/<file>.spec.ts       # one e2e file
pwsh scripts/tests/Invoke-ScriptTests.ps1                                                       # installer/script tests
pwsh scripts/check-migrations.ps1                                                               # migrations match the model
```

Keep output short: use `--filter`, a single file, or `| tail -30` rather than full-suite verbose logs.

## Rules that CI or tests enforce

- **Authorization:** the fallback policy only requires a signed-in user (= Viewer). Anything that writes, or
  exposes more than a Viewer should see, needs `.RequireAuthorization("RequireAnalyst")` or `("RequireAdmin")`.
  Every route and its access is listed in `RouteAuthorizationTests`; a new or changed route updates that list.
- **Graph permissions:** every Graph path the code calls must map to a permission in `graph-permissions.json`
  (`GraphPermissionCoverageTests`). Adding a call means adding the permission there and in `docs/graph-permissions.md`.
- **Tenancy:** Graph calls go through `GraphApiClient` / `TenantGraphCredentials` (per-tenant credentials in MSP).
  The client scopes requests with the `X-Vigil-Tenant` header via `apiFetch`; never call the API with bare `fetch`.
- **EF model change** = a migration for both `AppDbContext` and `PostgresAppDbContext` (commands in CONTRIBUTING.md).
- **Versions:** bump with `pwsh scripts/set-version.ps1 X.Y.Z` (API, installer and client must match).
- **CSP:** the browser may only connect to origins listed in `Program.cs` `connect-src`. New external origins go there.
- `VITE_E2E_FAKE_AUTH` is e2e-only; never put it in a `.env` file or a shipped build.
- No real tenant IDs, client IDs or secrets in commits; `appsettings.json` keeps placeholders.
- No new npm or NuGet packages without asking first.

## Conventions

- C#: records for results, small pure static evaluators (unit-tested) + thin endpoints. JSON from Microsoft is read
  tolerantly (case-insensitive, missing fields are OK) and never trusted to be complete; a partial read is surfaced as a
  note/flag, never shown as complete.
- Failures degrade per-section with a plain-English note to the admin, not a 500 for the whole page.
- UI copy: plain British English ("organisation"), short sentences, says what to do next. Every page has search,
  filter, sort and CSV export.
- Comments explain *why* (often a Microsoft quirk), not what. Match the density of the surrounding file.
- Update `CHANGELOG.md` (Unreleased) for user-visible changes; update `docs/THREAT_MODEL.md` when data flows or
  permissions change.

## Working efficiently

- Plans and status live in `docs/*_PLAN.md`; read only the one for the area you're touching.
- Microsoft API behaviour (paging hosts, field names, error codes) is a common source of bugs. Say when something is
  assumed rather than checked against a live tenant.
