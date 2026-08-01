# Changelog

All notable changes to Vigil365 are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The
version lives in exactly two places — the API's `<Version>` and the client's
`package.json` — kept in step by `scripts/set-version.ps1` and enforced in CI by
`scripts/check-version.ps1`.

## [Unreleased]

Work completed since the alert-first redesign, ordered by the maturity phase it
belongs to. Vigil365 remains **read-only**: it reports and recommends, and never
changes anything in the Microsoft 365 tenant.

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

## [1.0.0]

Initial self-hosted release: scheduled Microsoft Graph collection, metric,
activity and anomaly alert policies, Teams/email/webhook notifications, RBAC
with a tamper-evident audit trail, compliance assessment, trends, and the
executive digest.
