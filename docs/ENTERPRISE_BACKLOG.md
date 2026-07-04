# Vigil365 — Single-Tenant Enterprise App

The plan to make Vigil365 a **single-tenant, organisation-grade** product. This is
**Edition 1**. The **multi-tenant / MSP** edition is a separate, later effort —
see `MSP_MULTITENANT_PLAN.md` (Edition 2).

Scope: **alerts & visibility only — no remediation.** The app stays read-only
against the tenant (a deliberate, low-privilege selling point). "Recommendations"
mean *guidance + deep links*, never actions taken by the app.

Legend: 🔴 high value · 🟡 medium · 🟢 polish · ✅ already done

---

## Enterprise-ready: definition of done

Ship-the-edition gate — these must all be true to call it "Enterprise":

**Done ✅** — Microsoft sign-in + token validation · **enforced** RBAC
(Admin/Analyst/Viewer, deny-by-default fallback policy) + in-app user management +
invites · audit trail · HTTPS + encryption at rest · one-command install + Setup
wizard · Trends & history · Compliance framework scoring (configurable, no-data =
"Not assessed") · affected-entity on alerts · CSP header · finish §0 (perfect the
tabs) · audit hardening (IP/UA + SHA-256 hash chain + CSV export + verify + sign-in
events) · `/health` endpoint · structured JSON logging + correlation IDs ·
role-claim caching · data retention/pruning · rate limiting (300/min/IP) ·
config-driven CORS · bounded Graph 429 retries · demo-data honesty (opt-in seeding
+ auto-purge) · hash routing + alert permalinks · 8-section IA with tabs ·
alert engine: one open alert per policy, updated in place · design-system polish
pass (tokens, severity unification, dark mode, a11y foundations) · favicon + meta ·
37 automated tests.

**Required to ship 🔴** — certificate auth for Graph (replace client secret) ·
EF Core migrations (versioned upgrades) ·
**rotate the exposed client secret** *(owner action)*.

Everything else below is post-ship (v.next). Detail follows.

---

## 0. Perfect the existing tabs FIRST (before any new capability)

Polish what's already there — make every tab/detail genuinely useful — before
building new pages.

- ✅ **Alert detail must show the affected entity** — clicking a triggered alert
  shows affected entity list (UPN / device / detected at) with deep links. (Completed)
- ✅ **Every detail modal → drill to the real records** — audit each tab's detail
  view so it shows the underlying entities, not just summary fields. (Completed)
- ✅ **Consistent detail layout** — same field order, copy-to-clipboard on IDs,
  human-readable IDs/labels, relative + absolute timestamps everywhere. (Completed)
- ✅ **Cross-link** — from an alert → the user's/device's full record; from a KPI
  tile → the filtered list behind it. (Completed)
- ✅ **Per-tab audit pass** — walk every page (Identity, Devices, Email, Incidents,
  Compliance, CA, Licenses, Audit, Sign-in map) and fix the small gaps: empty
  columns, truncation, unclear labels, missing counts, broken/empty states. (Completed)

## A. New capabilities (cross-cutting)

- ✅ **Trends & history** — snapshot key metrics each collection cycle (risky users,
  MFA coverage, non-compliant devices, open critical/high, secure score, compliance
  issues). Dedicated page with executive KPI tiles, hero charts, insights, and clean PDF output. (Completed)
- ✅ **Compliance framework scoring** — map collected signals dynamically to **CIS Controls v8 / NIST CSF 2.0 / ISO 27001 / GDPR Art. 32** controls; live calculated posture scorecard with control breakdown modal + deep jump links. (Completed)
- ✅ **Recommendations layer** — every finding pairs with *why it matters*, *fix steps*,
  and a **deep link** to the right M365 portal blade. Guidance only, no actions. (Completed)
- ✅ **Alert coverage gap analysis** — compare the tenant against a best-practice
  alerting baseline and surface **what's NOT being watched**: e.g. no alert on
  privileged-role changes, mailbox forwarding rules, impossible-travel sign-ins,
  MFA-disabled admins, new OAuth app grants, sudden risky-user spikes. Two outputs:
  (1) one-click create the missing **Vigil365 Alert Center** policy from a template
  (in scope — app's own DB), and (2) recommend the missing **native** Defender /
  Entra / Purview alert policy with a **deep link** to create it in M365 (read-only —
  guidance, not an action). Extends the existing 9 alert templates into a coverage
  scorecard ("12 of 20 recommended alerts in place"). (Completed)
- 🟡 **Scheduled exec reports** — weekly/however email digest (PDF/HTML) summarising
  posture + trends, for leadership. Reuses SMTP.
- 🟡 **Conditional Access gap analysis** — surface users/apps **not** covered by any CA
  policy, MFA-exempt accounts, legacy-auth exposure.
- 🟡 **Entity drill-down** — click a user/device to see its full timeline (alerts,
  sign-ins, risk history) on one detail page rather than a single modal.
- 🟡 **Zero Trust assessment score** — map signals to Microsoft's Zero Trust pillars
  (identity, devices, apps, data, network, infrastructure) with a per-pillar readiness
  score + gaps. Complements the compliance-framework scoring. (Erik's early suggestion.)
- 🟡 **SharePoint & OneDrive monitoring** — external sharing, anonymous links, DLP
  matches, oversharing signals (Graph read-only). New coverage area. (Curt Blunt's ask.)
- 🟢 **Teams security** — guest access, external federation, risky app/connector
  permissions across Teams.
- 🟢 **Global search** — cross-page search (user/device/alert) from the header.

## B. Per-page depth gaps

- 🟡 **Identity** — guest/external account governance, stale-account list, passwordless/
  MFA-method breakdown, per-user risk timeline.
- 🟡 **Devices** — vulnerability/patch posture, OS/version breakdown, config-drift view.
- 🟡 **Email** — top-targeted users, threat trend over time, per-message detail (read-only).
- 🟡 **Incidents** — **MITRE ATT&CK mapping**, incident ownership/assignment, severity
  trend, link related alerts.
- 🟡 **Licenses & Users** — license **cost-optimization** (unused/duplicate SKUs, savings).
- 🟢 **Service Health / Audit / Sign-in map** — roughly at parity; add date-range presets.

## C. UI/UX polish (concrete, found in review)

- ✅ **Error boundary** — top-level boundary with theme-aware friendly card; stack
  trace behind a "Technical details" disclosure. (Completed; per-page fallback still open.)
- ✅ **Loading skeletons** — pulse skeleton system (kpi/table/list/card) + DashboardSkeleton. (Completed)
- ✅ **Accessibility (foundations)** — dialog role + focus trap + focus return
  (DetailModal), aria-live toasts, :focus-visible on all buttons, th scope=col,
  card titles as h2, aria-current nav, aria-labels on icon buttons. (Completed;
  remaining: keyboard access on clickable list rows, trap parity for PolicyModal +
  triggered-alert modal.)
- 🟡 **Keyboard navigation** — partial: Esc closes modals, focus trap in DetailModal.
  Still open: arrow-key nav in lists, Enter on clickable rows app-wide.
- ✅ **Empty vs error vs no-permission states** — unified StateMessage component
  (empty / error / permission variants). (Completed)
- 🟡 **Large-list performance** — long tables render all rows; add virtualization or
  server paging for big tenants.
- ✅ **Favicon + tab branding** — shield SVG favicon, theme-color, apple-touch-icon. (Completed)
- ✅ **index.html meta** — description / Open Graph / theme-color. (Completed)
- 🟢 **Consistent number/date formatting** — thousands separators, consistent relative
  vs absolute time, explicit timezone label (UTC vs local) on every timestamp.
- 🟢 **Toasts** — make dismissible, stack, `aria-live=polite`, auto-expire consistently.
- 🟢 **Sticky table headers** on long lists; column min-widths to stop layout shift.
- 🟢 **Per-card "last updated" + manual refresh** affordance.
- 🟢 **Density toggle** (comfortable/compact) for big tenants.
- 🟢 **Mobile pass** — verify every page below 900px (sidebar drawer, table → cards).
- 🟢 **Tooltips/help** — explain each metric (what "risky user" means, thresholds).

## D. Data quality / correctness details

- 🟡 **Timezone clarity** — label timestamps UTC vs local; let user pick. (Formats are
  now unified: two formatters, year-aware, en-US pinned — timezone labeling still open.)
- ✅ **Stale-data indicator** — Overview status banner: in-progress / failed / stale
  (>3 cycles) / fresh, with source-failure counts. (Completed)
- 🟢 **Pagination/total counts** consistent across every list.
- 🟢 **CSV export parity** — ensure every table's export matches the visible/filtered rows.
- 🟢 **Deep-link correctness** — verify each "View in M365 portal" link resolves.

## F. Gaps surfaced later (alerting depth, integrations, edge cases)

**Alert workflow** (core to an alerting product)
- 🔴 Assignment / ownership per alert.
- 🟡 SLA tracking (time-to-ack / time-to-resolve) + escalation if unacked.
- ✅ Deduplication (policy alerts) — one open alert per policy, updated in place
  while breached; duplicates auto-collapsed. (Completed. Cross-source root-cause
  correlation still open.)
- 🟡 Comments / notes on an alert (collaboration).
- 🟡 Maintenance windows / quiet hours (deferred from the snooze PR).

**Notifications**
- 🟡 More channels: native Slack, PagerDuty, ServiceNow, SIEM forward (Sentinel/Splunk).
- 🟡 Daily/weekly digest mode (reduce per-alert noise).
- 🟡 Per-user / per-role notification preferences.
- 🟡 Alert on notification **delivery failure** (don't fail silently).

**Sovereign / government clouds** 🔴
- Graph + login endpoints are hardcoded to commercial cloud
  (`graph.microsoft.com` / `login.microsoftonline.com`). GCC, GCC High, and
  21Vianet use different endpoints — app won't run there. Make cloud-environment
  configurable.

**MSP / multi-tenant** (large audience — currently deferred)
- 🟡 White-label / branding (org logo on dashboard + reports).
- 🟢 Clear "single-tenant by design, MSP multi-tenant on the roadmap" stance.

**Integration / openness**
- 🟡 Read **API** for external consumption (other tools / dashboards).
- 🟡 Config + policy **export/import** (portability + backup beyond the DB).

**Supply-chain & release trust** (enterprises ask)
- 🟡 **SBOM**, signed releases, dependency scanning.
- 🟢 Versioned releases + semver + changelog.

**Session & quality**
- 🟡 Idle timeout / auto sign-out.
- 🟢 In-app version ✅ (sidebar + login footer chip) · changelog; opt-in telemetry;
  i18n; formal WCAG audit — still open.

## E. Enterprise plumbing (tracked, lower priority for this product pass)

- ✅ **Audit hardening** — IP/user-agent capture, sign-in + alert events, tamper-evident
  SHA-256 hash chain, CSV export + verify endpoint, retention. (Completed; collection
  history lives in CollectionRuns rather than the audit trail to avoid 15-min noise.)
- ✅ **/health endpoint** (DB + Graph + last-collection freshness; 503 when DB down). (Completed)
- ✅ **Role-claim caching** (60s TTL, evicted on role change/removal). (Completed)
- 🟡 **Setup permission verification** — check each required Graph permission is granted
  (fixes ambiguous "Needs permission" on Secure Score).
- ✅ **Structured logging + correlation IDs** (JSON console outside Dev, X-Correlation-Id
  echo + logging scope). (Completed)
- ✅ **Rate limiting** — fixed-window 300 req/min per client IP. (Completed)
- ✅ **Data retention/pruning** — nightly worker, per-dataset day windows in the
  `Retention` config section; open alerts never pruned. (Completed)
- 🟢 **EF migrations** instead of EnsureCreated + raw DDL for versioned upgrades.

---

## Suggested build order
1. **Error boundary + loading skeletons + empty/error/permission states** (foundation; touches every page)
2. **Trends & history** (Craig's ask; unlocks Overview trend cards + exec reports)
3. **Compliance framework scoring** (differentiator)
4. **Recommendations layer** (guidance + deep links)
5. **Accessibility + keyboard pass**
6. **CA gap analysis**, per-page depth, drill-down
7. **Audit hardening + /health** (plumbing)
8. **Polish**: favicon/meta, formatting, tooltips, density, mobile

> Out of scope (deliberate): remediation actions (confirm-compromised, quarantine,
> isolate), multi-tenant SaaS, raw-log SIEM ingestion.
