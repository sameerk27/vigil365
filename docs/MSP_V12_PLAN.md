# Vigil365 v1.2 — MSP Mode & UI Completion Plan

**Status:** Stage 0 ✅ (draft PR #8, CI green incl. first PostgreSQL run — which caught and
fixed an audit-hash bug on Postgres). Stage 1 ✅ (M1 mode flag + sign-in pin, T1 component
harness, T2 signed-in e2e in CI). Stage 2 ✅ (U1 Choose-a-client gate,
U4 consent-poll cleanup, U5 client named in header/toasts/exports, M2 web register path
removed + MSP app readiness card + one `graph-permissions.json`). Stage 3 ✅ (M3 installer MSP mode + PostgreSQL + Express block, M4 convert-to-MSP by
re-running Setup, M5 -Mode/-DatabaseProvider in deploy/enterprise scripts, Postgres compose = MSP).
Stage 3 is built and unit-tested but **not yet run on a real machine** — that is R1. Stage 4 ✅ (U6 cross-client queue, U7 API token UI with client restriction, U8 DB-size banner, U9 MSP audit Client column, U10–U13 — see notes under the table). Next: Stage 5. Combines and replaces `MSP_MODE_PLAN.md` and
`MSP_UI_PLAN.md`. Builds on `MSP_EDITION_PLAN.md` (backend Phases 1–8 + one-go
onboarding — implemented, uncommitted on `feat/msp-phase1-db-providers`).

**Purpose:** take the MSP edition from **built** to **installable and verified**.

---

## 0. Where we are

| Area | State |
|---|---|
| MSP backend (isolation, credentials, routing, overrides, digest, hardening, consent landing) | Built. 296 .NET tests pass, incl. isolation suite on real SQL Server. **PostgreSQL never executed.** |
| Installer | **Single-tenant only.** Creates a single-tenant app (can't accept client consent), defaults to SQL Express (unsupported for MSP), no PostgreSQL option, no MSP mode. |
| In-app *Register the shared MSP app* | **Always fails on installed copies** (`register-app.ps1` not shipped), and that script requests a **stale permission list**. |
| MSP UI (Clients, switcher, onboarding, assignment, routing, overrides) | Built, compiles, bundle builds. **Never rendered or clicked.** All 94 API routes have UI callers (only SIEM, `/health`, `/consented` don't — by design). |
| UI tests | **None for MSP.** Vitest = pure functions only; no React Testing Library; Playwright e2e can't get past Microsoft sign-in. |
| Git | 104 files uncommitted. |

---

## 1. Target

**One installer, two modes, chosen up front.**

| | Single organisation (Edition 1) | MSP — multiple clients (Edition 2) |
|---|---|---|
| App registration | single-tenant | **multi-tenant** + Web redirect `{url}/consented` |
| Who can sign in to Vigil365 | your tenant | **the MSP's own tenant only** |
| Database | SQL Express (default) or SQL Server | **SQL Server (non-Express) or PostgreSQL** |
| UI | exactly v1.1.0 | Clients section, switcher, onboarding, cross-client queue |
| First client | your tenant, automatically | added from the Clients page |

**Done means:** an MSP runs `Vigil365-Setup.exe`, picks **MSP**, and in one sitting onboards a
real client via *Sign in as global admin & consent* — no script, no portal, no hand-edited
config — and every MSP screen involved is covered by automated tests and the release
walkthrough (§6).

---

## 2. Decisions

**D1 — Mode is a config value, `Edition:Mode` = `Single` | `Msp`, chosen at install.**
Default `Single` (every existing install unchanged). Exposed in `/api/auth/config`; the
client gates all MSP UI on it; the API refuses a second client in `Single` mode.

**D2 — The installer creates the multi-tenant app, not the web app.** The installer already
runs Azure CLI as the operator (`GuiInstaller/MainWindow.xaml.cs:811`) and already owns the
correct permission list (`GraphPermissions.cs`). MSP mode there = `AzureADMultipleOrgs` +
a `web.redirectUris` entry.

**D3 — One app registration for sign-in and collection; sign-in pinned to the MSP tenant.**
`AzureAd:TenantId` is the MSP's GUID, so foreign-tenant tokens fail issuer validation —
proven by an explicit test (M1), not assumed. (Two registrations = fallback only.)

**D4 — MSP mode accepts only server-grade databases:** SQL Server Standard/Enterprise/Azure
SQL or PostgreSQL 14+, bring-your-own. Express blocked with the reason
(`SERVERPROPERTY('EngineEdition') = 4`).

**D5 — One Graph permission list.** `GraphPermissions.cs` is the source; emitted as
`graph-permissions.json`; `register-app.ps1` reads it; CI checks `docs/graph-permissions.md`.
Must land before any client consents — changing permissions later forces every client to
re-consent.

**D6 — Delete `POST /api/setup/register-msp-app`** (and its button); replace with a
read-only *MSP app status* card pointing at the installer's *Convert to MSP*.

**D7 — Fail closed stays; the UI handles it.** With ≥2 clients and none selected the API
returns 400 by design. The UI must turn that into a *pick a client* state, never an error wall.

**D8 — Switching client keeps the full reload** (no stale cross-client state), but preserves
the current page and filters.

**D9 — UI testability via a test-only fake-auth flag** (`VITE_E2E_FAKE_AUTH`) that bypasses
MSAL in e2e builds; CI asserts the release bundle does not contain it.

---

## 3. Work items

Priority **P0** = must have for v1.2 · **P1** = ships with v1.2 · **P2** = after.
Size: S ≈ ½ day · M ≈ 1–2 days · L ≈ 3–4 days.

### Stage 0 — Prerequisite

| ID | Item | Size |
|---|---|---|
| S0 | Commit the branch in reviewable commits (by phase, incl. all untracked files), push, open PR, get the **first green CI** — first ever live PostgreSQL run (migrations, parity, isolation). | M |

### Stage 1 — Foundations (everything else depends on these)

| ID | Item | Where | Size |
|---|---|---|---|
| M1 | **Mode flag & gating.** `EditionOptions`; `mode` in `/api/auth/config`; 409 on 2nd client in Single; `/consented` + consent-url 404 in Single; client hides Clients section, switcher, client-only toggle, routing card, override column, assignment column unless `Msp`. **Test: foreign-tenant token → 401.** | `Program.cs`, `AuthHealthEndpoints.cs`, `TenantEndpoints.cs`, `main.tsx`, `TenantSwitcher.tsx`, `AlertCenterPage.tsx`, `UserManagementPage.tsx`, new `AuthIssuerTests.cs` | M |
| T1 | **Component-test harness.** Add React Testing Library + user-event + jest-dom; jsdom for `*.test.tsx`; a `fetch` mock keyed by URL. | `package.json`, vitest config, `src/test/` | M |
| T2 | **E2E past sign-in.** `VITE_E2E_FAKE_AUTH` seam (D9); Playwright `page.route()` API fixtures; CI check that release bundle lacks the flag. | `main.tsx`/`api.ts`, `e2e/`, `ci.yml` | M |

### Stage 2 — P0 product fixes (each lands with tests)

| ID | Item | Size |
|---|---|---|
| U1 | **No-client-selected state.** With ≥2 clients and none selected, land on Clients with a *pick a client* prompt; tenant-scoped pages show the same prompt instead of error cards (D7). | M |
| U4 | **Consent poll cleanup.** Clear the interval on dialog close/unmount (`ClientsPage.tsx:312-318`); test the unmount case. | S |
| U5 | **Always show which client.** Client name in the page header, in CSV/PDF export filenames, and in action toasts. | S |
| M2 | **Remove web register path; one permission list.** Delete endpoint/button (D6); add *MSP app status* card (`GET /api/setup/msp-app-status`); `graph-permissions.json` + `register-app.ps1` reads it + CI check (D5). | M |

### Stage 3 — Installer & deployment

| ID | Item | Where | Size |
|---|---|---|---|
| M3 | **Installer MSP mode (fresh install).** Mode choice on the Configuration step; MSP → `AzureADMultipleOrgs` + `/consented` web redirect; database engine choice (SQL Server / existing PostgreSQL); Npgsql validation, skip SQL-login grant for Postgres; block Express in MSP; write and **preserve on re-run** `Database:Provider` + `Edition:Mode`; MSP completion page → "Clients → Add client". | `GuiInstaller/MainWindow.xaml(.cs)`, `DatabaseSetup.cs` | L |
| M4 | **Convert existing Single install → MSP.** PATCH the existing app (audience + redirect, no new app/secret); set mode; refuse on Express with migration guidance; prompt to rename "Default" client. | `GuiInstaller` | M |
| M5 | **Scripts & containers.** `-Mode` and `-DatabaseProvider` on `deploy.ps1`, `install.ps1`, `enterprise-install.ps1/.sh`; `docker-compose.postgres.yml` sets `Edition__Mode=Msp`. | scripts, compose | S |

### Stage 4 — P1 product completeness (each lands with tests)

| ID | Item | Size |
|---|---|---|
| U6 | **Cross-client alert queue** ("All clients"): open triggered alerts across permitted clients, client column, sort/filter; click → switch client and open the alert. Planned in `MSP_EDITION_PLAN.md` Part 5, never built. | M |
| U7 | **SIEM token client restriction in UI** (picker on create, shown on existing tokens). Backend already supports it. | S |
| U8 | **Database-size warning in UI** (System/Metrics tab + Admin banner when `/health` reports `sizeWarning`). | S |
| U9 | **Audit log in MSP mode** — scope per decision Q4 (§7); integrity verify/export stay whole-chain. | M |
| U10 | **Preserve page and filters across client switch** (D8). | S |
| U11 | **Onboarding error states:** popup blocked, consent declined, consented in wrong tenant, test failed after consent — each with a distinct message and retry. | S |
| U12 | **Loading / empty / error states** on roster, rollup, routing card, override column, assignment picker — consistent with `SharedComponents`. | S |
| U13 | **Non-admin experience** for 0 / 1 / many assigned clients; every Admin-only control hidden. | S |

**Stage 4 as built — honest gaps:** U10 keeps the page (hash survives the reload) but **not** in-page filters; they reset on switch. U11 covers popup blocked, window closed, consent declined (the tenant's recorded error), and timeout inline; "consented in the wrong tenant" and "test failed after consent" surface through the existing connection-test result, not a dedicated message. U12 covers rollup, routing card and the cross-client queue; the roster table and assignment picker still use their older empty states. U13 also fixed a real bug: a user whose only client was resolved by the server never had it recorded, so per-client controls stayed hidden; and Viewers no longer see policy Edit/Delete (the server requires Analyst).

### Stage 5 — Docs, accessibility, release gate

| ID | Item | Size |
|---|---|---|
| D1d | README "Install for an MSP" + Docker Postgres route; ADMINISTRATION: one-go onboarding (manual path as appendix); SECURITY.md: both trust models; MSP_DATA_PROCESSING.md: no "verified on PostgreSQL" claim until S0 proves it; OPERATIONS_RUNBOOK: `pg_dump`/`pg_restore`, Convert-to-MSP, "v1.2 migrations are one-way — rollback = restore". | M |
| T3 | **Accessibility:** extend the axe run to every MSP component (dialog focus, popup button, switcher label, table headers, colour-only status on rollup cards). | S |
| R1 | **Release walkthrough** (§6) on a packaged build — and fix what it finds. | M |
| R2 | Version 1.2.0 (`scripts/set-version.ps1`), finalize CHANGELOG, rebuild installer, tag. Delete the stale Sep-10 `dist/` exe. | S |

### Later — P2

| ID | Item | Size |
|---|---|---|
| U14 | Dark mode (existing design TODO, `main.tsx:276,627`) | M |
| U15 | Brand preview in onboarding | S |
| U16 | Peek a client from its rollup card without switching | M |
| U17 | Bulk roster actions (deactivate many, re-test all) | S |

---

## 4. Order

```
S0 commit + first green CI
 │
 ├─ M1 mode flag ─┬─ U1 · U4 · U5 ──────────────┐
 │                └─ M3 installer ─ M4 convert ─┤
 ├─ T1 component harness ───────────────────────┤
 ├─ T2 e2e fake-auth ───────────────────────────┤
 └─ M2 remove web path / permissions ───────────┤
                                                ├─ U6…U13 (P1) ── T3 a11y
                     M5 scripts/containers ─────┤
                                                ▼
                                D1d docs ── R1 walkthrough ── R2 release v1.2.0
```

Two tracks run in parallel after S0: **installer** (M3 → M4 → M5) and **app/UI**
(M1 → T1/T2 → U1/U4/U5 → U6…U13). M1 must land before any UI gating; D5 (inside M2)
before any real client consents.

---

## 5. Effort

| Stage | Items | Days |
|---|---|---|
| 0 | S0 | 1 |
| 1 | M1, T1, T2 | 5–6 |
| 2 | U1, U4, U5, M2 | 3–4 |
| 3 | M3, M4, M5 | 6–7 |
| 4 | U6–U13 | 6–7 |
| 5 | D1d, T3, R1, R2 | 4–5 |
| **Total** | | **≈ 25–30 days** one developer · **≈ 3.5–4 weeks** with the two tracks in parallel |

P2 not included.

---

## 6. Release gate — the walkthrough that has never happened

On a **packaged build**, with one real client tenant you control. Every line must pass.

**Install**
1. Fresh VM + existing PostgreSQL → Setup → **MSP** → install completes; config has
   `Database:Provider=Postgres`, `Edition:Mode=Msp`; app registration is multi-tenant with
   `/consented` redirect and the D5 permission list.
2. Same with SQL Server Standard. Express connection in MSP mode is refused with the reason.
3. Separate VM, **Single** mode → looks and behaves exactly like v1.1.0 (no Clients, no switcher).
4. v1.1.0 install with data → upgrade → **Convert to MSP** → history preserved as first client.

**Use**
5. Sign in → lands on Clients, no error wall.
6. Add client → *Sign in as global admin & consent* → approve → dialog shows connected;
   roster shows Entra id + consent time.
7. A user from the **client** tenant cannot sign in to Vigil365.
8. After one collection: rollup card has real counts; selecting the client populates
   Overview/Alerts/Identity/Devices; client name visible on every page and export.
9. Second client → switcher lists both; switching keeps the page; cross-client queue
   shows both with correct client column.
10. Viewer assigned to client 1 only → cannot see client 2 anywhere (switcher, rollup,
    queue, URLs, exports).
11. Routing: client-only email → test alert reaches the client, not the MSP.
12. Override: default policy off for client 1 → still fires for client 2.
13. MSP digest at the configured hour → one email, both clients, worst first.
14. Branding → scheduled report email/CSV/PDF carry the client brand.
15. Deactivate client 2 → hidden, data kept; re-activate → back. Purge a test client →
    its data gone, the other client untouched.

**CI (automated, must be green):** .NET suite on SQL Server **and PostgreSQL**; component
tests (T1); e2e journeys (T2); axe (T3); migration drift check; release-bundle has no
fake-auth flag.

---

## 7. Open questions

| # | Question | Recommendation |
|---|---|---|
| Q1 | Installer installs PostgreSQL locally in MSP mode? | **No** — bring your own for v1.2; document it. |
| Q2 | Grant `Application.Read.All` on the **MSP's own tenant** for the app-status card (M2)? | Yes on the MSP tenant only (never in what clients consent to); otherwise card shows "unknown". |
| Q3 | Can **clients** log in to see their own data? | Not in v1.2 (sign-in is pinned to the MSP tenant). Remove the claim from `MSP_DATA_PROCESSING.md`; revisit via B2B guests later. |
| Q4 | MSP Admin audit log: one list across all clients (with Client column) or per client? | One list for MSP Admins, per-client for others. |
| Q5 | Act on alerts directly from the cross-client queue, or switch into the client first? | Act in place, with the client name in the confirm/toast — speed matters for triage; U5 covers wrong-client risk. |
| Q6 | Accept a test-only fake-auth flag in the client build (D9)? | Yes, with the CI assertion that release bundles lack it. |

## 8. Out of scope for v1.2

Per-client billing/licensing, GDAP/Partner Center, per-client data residency, installing
PostgreSQL, client logins, and any remediation capability (Vigil365 stays read-only).
