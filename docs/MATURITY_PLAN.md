# Vigil365 — Product Maturity Plan (July 2026)

Consolidated from four reviews: CISO security pass, principal-developer architecture
pass, product-maturity/QA pass, and UI/UX critique. Ordered small → big; each phase
is shippable. Security Phase A items are interleaved where they belong.

> **Why earlier reviews missed things (process note):** every audit so far was run by
> the same context that built the features — each pass inherits the builder's blind
> spots and checks what it already knows to check. Corrective: independent fresh-eyes
> reviews (no shared assumptions) at the end of every phase, plus one session watching
> a real unfamiliar user per phase. Findings from the first independent sweep:
> CSV formula injection (3 exporters), 96 compiled DLLs committed in
> `publish-install-test/` — both invisible to prior passes.

## Phase M0 — Confirmed bugs, fix first (hours) — from independent fresh-eyes audit
0a. **Tenant Activity page rendered twice** — duplicate `{page==="activityfeed"...}`
    at main.tsx:557 AND :566; delete one (double fetch + double render).
0b. **"Tampering detected" shows as GREEN success toast** — UserManagementPage.tsx:147
    omits toast type; the scariest message in the product looks like success. Add "error".
0c. **Setup validation error shows as success toast** — SetupPage.tsx:30, same fix.
0d. **Overview "Total Active" vs queue disagree past 200 alerts** — Overview uses full
    DB count (Program.cs:705), queue fetches pageSize=200 (main.tsx:323) with no
    truncation indicator. Show "showing 200 of N" or reconcile the counts.
0e. **API failures invisible mid-session** — allSettled swallows every fetch error and
    still stamps "Updated HH:MM" over stale cards (main.tsx:301-352); surface per-card
    load failure + don't advance lastRefresh on total failure.
0f. **Entity investigation page unreachable except via Ctrl+K** — add "Open
    investigation profile" from AlertDetailModal (openEntity only called in GlobalSearch).
0g. **info toasts render as success (green check)** — ToastContainer.tsx:24-26; give
    info its own neutral styling.

## Phase M1 — Small details, big signal (days)
1. **CSV formula-injection guard** in all three exporters (utils.ts `downloadCsv`,
   DigestBuilder.Csv, audit-export Csv in Program.cs) + contract test. *(security)*
2. Purge committed `publish-install-test/` build output; gitignore it.
3. Replace 3 native `confirm()` (AlertCenter, Reports, UserManagement) with in-app modal.
4. Timezone labels + single `fmtDate` policy (local + UTC offset, ISO tooltip).
5. Error banner → tokenized styles + surface correlation ID ("quote ID X to admin").
6. Per-card "updated Xm ago" + auto-ticking relative times.
7. **Standardized `<CardState>` component** (loading/empty/error/no-permission with
   retry affordance) — adopt in all cards. *(UX — added)*
8. 403-permission-hint pattern on every Graph-backed endpoint.
9. Idle timeout + absolute session expiry. *(security A2)*
10. Unknown `/api/*` → 404 (SPA fallback only for non-API paths). *(security A3)*
11. CI gates: `dotnet list package --vulnerable` + `npm audit --audit-level=high`.

## Phase M2 — First hour (~1 week)
12. First-run checklist card (consent / permissions / SMTP / policies / first
    collection) driven by /health + settings.
13. Setup wizard: add SMTP + notification-channel step.
14. In-app help links per page header; permission-matrix page from graph-permissions.md.
15. **Overview visual hierarchy: one dominant triage-status element** — needs owner
    sign-off (a "Needs Attention" card was previously removed by owner). *(UX — added)*

## Phase M3 — Alert-ops maturity (1–2 weeks)
16. Suppression rules (entity/policy scoped, expiring, audited) — top alert-fatigue fix.
17. Policy dry-run/backtest against stored history ("would have fired N times in 30d").
18. MTTR & analyst metrics tab (data already stored: ack/resolve timestamps).
19. Policy export/import (JSON pack).
20. Server-side-only policy evaluation; remove client trigger. *(security B6)*

## Phase M4 — Enterprise fit (1–2 weeks)
21. ✅ PDF export for exec digest; multi-recipient schedules.
22. ✅ Outbound API tokens + signed generic webhook-out (SIEM path).
23. ✅ Keyboard/a11y pass: table navigation, sortable-column announcements, skip-link, axe checks in CI.
24. ✅ Density toggle (compact/comfortable) + formalized typography scale. *(UX)*
25. 🟡 Remove remaining inline styles → drop `style-src 'unsafe-inline'` from CSP. In progress: the Reports page and shared shell are converted; 407 inline-style props remain across the frontend.

## Phase M5 — Product machinery (ongoing; gates "1.0")
26. Tagged releases + changelog + upgrade-from-N−1 test; version chip from build metadata.
27. [x] Playwright smoke suite in CI (login → overview → bundle-hash assert).
28. Metrics endpoint (collection duration, Graph call/429 counts).
29. String centralization (future i18n).
30. Program.cs split into endpoint-group modules. *(architecture B5 — do before M3/M4
    backend work if merge pain appears earlier)*
31. TanStack Query migration; pagination envelope + virtualization; route code-splitting.

## Standing items (every phase)
- Independent fresh-eyes review at phase end (agent or human uninvolved in the build).
- **Watch one real unfamiliar user complete a triage flow; log every hesitation.** *(added)*
- Update this doc + ROADMAP.md as items land.

## Trust-in-numbers cluster (M1/M2 — from UX audit; a security tool's credibility)
- Device compliance computed 3 different ways (Overview / DevicesPage / Compliance
  control PR.DS-01) — pick one source of truth.
- Compliance controls that auto-pass on data-collected (PR.PT-02, DE.AE-01) or can
  never pass (PR.AA-02 fails at 0 privileged users) — the headline "Controls Passing %"
  is partly fake. Redefine pass logic per control.
- Identity KPI tiles reflect the *filtered* list and default "show resolved" ON — tiles
  change as you type and exceed Overview's unresolved-only counts.
- ServiceHealth advisories double-counted / inconsistently included across queue,
  Overview, sidebar badge — one inclusion rule.
- Sign-in "totals" (countries, failed sign-ins) computed from a sample of 100 but
  labeled tenant-wide; NetworkPage derives "connectivity" from auth failures.
- Compliance thresholds + filter presets live in per-browser localStorage — two
  analysts see different scores for the same tenant. Move to server or label as local.
- Exports export the visible page, not the dataset, while the badge shows the full total.
- Bulk ack/resolve reports only successes, hiding partial failures.

## Consistency/copy cluster (M1 — from UX audit)
- In-app alert source has 3 names ("Vigil365 Alerts"/"SecurityDB"/"In-App DB"); nav/card/
  README disagree too. Pick one.
- 5 different severity vocabularies across filter dropdowns; CA controls show raw enum
  tokens ("mfa","compliantDevice"); ReportsPage uses nonexistent `data-table` class.
- 3 pages break the pinned en-US date convention (header, Reports, Trends).
- Sidebar badges mean "unread delta" (unexplained) — every competitor means "open items".

## README/docs vs reality (M2 — from UX audit)
- README promises a geographic sign-in MAP that doesn't exist (page is tables only).
- Claims all permissions are *.Read.All (Attack Simulation needs ReadWrite.All);
  permission table omits several the UI actually requires.
- Documents 4 endpoint paths that don't exist; misdescribes triggered alerts as
  localStorage; contradicts the Recommendations page ("never tells you what to change").
- Never documents Reports, Trends, Recommendations, RBAC, notification channels — the
  strongest demo features. Rewrite the README against the actual app.

## Security pass results (inline audit, July 2026) — mostly clean
- **Confirmed clean:** every mutating endpoint role-gated (RequireAdmin/RequireAnalyst)
  + deny-by-default fallback; last-admin lockout guard server-side; email HTML bodies
  HtmlEncode all tenant-controlled fields, Teams/webhook use JsonSerializer (injection-
  safe); secrets never logged, DPAPI-encrypted at rest; audit hash-chain append
  serialized via SemaphoreSlim; 0 vulnerable NuGet/npm packages; setup/graph +
  collector/run admin-gated.
- **🟠 M3 — SSRF hardening:** no validation that configured Teams/generic webhook URLs
  aren't loopback/RFC1918/link-local/cloud-metadata (169.254.169.254). Admin-gated so
  Medium, but a security product should validate egress. Reject internal targets or
  document the trust boundary.
- **🟡 M5 — audit fail-open:** AuditLogger swallows persistence errors (action succeeds
  even if the audit write fails). Consider a monitored failure counter / fail-closed
  option for the audit trail specifically.
- **Note:** audit ChainLock is an in-process static — correct for single-instance;
  revisit if ever horizontally scaled (ties into the MSP edition).

## Known-external gaps (cannot be closed by code review)
Real-tenant scale validation (10k+ alerts) · third-party pen test · genuine usability
testing. State these honestly in ORG_READINESS.md rather than claiming coverage.

## Open owner action 🔴
Rotate the exposed Graph client secret in Entra (outstanding since July 3).
