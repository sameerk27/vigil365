# Contributing to Vigil365

Thank you for your interest in contributing! This project is built and maintained by an IT professional to help other M365 admins. All skill levels are welcome.

## How to Contribute

### Report a Bug
1. Go to [Issues](https://github.com/sameerk27/vigil365/issues)
2. Click **New Issue** → **Bug Report**
3. Describe what happened and what you expected

### Suggest a Feature
1. Go to [Issues](https://github.com/sameerk27/vigil365/issues)
2. Click **New Issue** → **Feature Request**
3. Describe what you'd like to see and why it would help M365 admins

### Submit a Code Change
1. Fork the repo
2. Make your changes
3. Make sure the app builds without errors (`npm run build` in the client folder)
4. Open a Pull Request — describe what you changed and why

### Run the Tests

```bash
dotnet test src/M365SecurityDashboard.Api.Tests
```

Most tests use EF's in-memory provider and run anywhere in seconds. The
`Relational/` parity suite additionally starts a real SQL Server and a real
PostgreSQL in Docker and runs the same assertions on both — this is the only
place engine-specific behaviour (index filters, identity columns, column types,
the size query, and later tenant-isolation filters) is actually verified.
Without Docker those tests **skip** with a reason; CI sets
`VIGIL365_REQUIRE_DB_TESTS=1` so a missing Docker fails the build instead.

The installer scripts have their own tests, which install nothing:

```bash
pwsh scripts/tests/Invoke-ScriptTests.ps1      # register-app.ps1 against a fake az; install*.ps1's sc.exe command line
bash scripts/tests/enterprise-install.test.sh  # the JSON enterprise-install.sh writes
```

The Pester tests run with Pester 3.4 (built into Windows PowerShell 5.1) or 5.x;
CI also runs them under Windows PowerShell 5.1, where, elevated, they create and
delete a real service. The setup wizard's decisions (database choice, ACLs,
replacing a running service's files) live in `InstallPlan.cs` and are tested in
`InstallPlanTests`; the WPF window itself has no automated test.

Any change to the EF model needs a migration for **both** engines:

```bash
dotnet ef migrations add <Name> --project src/M365SecurityDashboard.Api --context AppDbContext --output-dir Data/Migrations
dotnet ef migrations add <Name> --project src/M365SecurityDashboard.Api --context PostgresAppDbContext --output-dir Data/Migrations/Postgres
```

`scripts/check-migrations.ps1` (run in CI) fails if either set is missing.

## Rules

- **No credentials** — never commit real Tenant IDs, Client IDs, or secrets
- `appsettings.json` must keep placeholder values (`YOUR_TENANT_ID` etc.)
- `VITE_E2E_FAKE_AUTH=1` (sign-in bypass) is for the signed-in e2e tests only, and works
  only on the vite dev server: `vite build` drops it even when the flag is set (CI checks
  this), and `scripts/build-installer.ps1`, the Docker build and CI also refuse a bundle
  that contains it. Keep it out of client `.env` files all the same — `npm run dev`
  would skip sign-in.
- No new npm packages without discussion in an Issue first
- Keep it focused on M365 / Microsoft Graph — out of scope: other cloud providers

## Questions?

Open an [Issue](https://github.com/sameerk27/vigil365/issues) and ask — no question is too basic.
