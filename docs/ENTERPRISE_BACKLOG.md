# Vigil365 — Enterprise Maturity Backlog

Everything to add/fix to make the dashboard enterprise-grade, **before** building.
Scope decision: **alerts & visibility only — no remediation actions.** The app
stays read-only against the tenant (a deliberate, low-privilege selling point).
"Recommendations" mean *guidance + deep links*, never actions taken by the app.

Legend: 🔴 high value · 🟡 medium · 🟢 polish · ✅ already done

---

## 0. Perfect the existing tabs FIRST (before any new capability)

Polish what's already there — make every tab/detail genuinely useful — before
building new pages.

- 🔴 **Alert detail must show the affected entity** — clicking a triggered alert
  shows policy metadata (ID, condition, threshold) but **not which user/device
  triggered it**. Root cause: the engine stores the aggregate count, not the
  matching entities. Fix: at trigger time, capture the affected `SecurityAlert`
  rows (UPN / device / sign-in) and store them with the triggered alert; render
  them in the detail modal as a list with "view in M365 portal" deep links.
- 🟡 **Every detail modal → drill to the real records** — audit each tab's detail
  view so it shows the underlying entities, not just summary fields.
- 🟡 **Consistent detail layout** — same field order, copy-to-clipboard on IDs,
  human-readable IDs/labels, relative + absolute timestamps everywhere.
- 🟡 **Cross-link** — from an alert → the user's/device's full record; from a KPI
  tile → the filtered list behind it.
- 🟢 **Per-tab audit pass** — walk every page (Identity, Devices, Email, Incidents,
  Compliance, CA, Licenses, Audit, Sign-in map) and fix the small gaps: empty
  columns, truncation, unclear labels, missing counts, broken/empty states.

## A. New capabilities (cross-cutting)

- 🔴 **Trends & history** — snapshot key metrics each collection cycle (risky users,
  MFA coverage, non-compliant devices, open critical/high, secure score, compliance
  issues). New page with line charts + per-metric **up/down/flat chip** and a plain-
  language readout ("Risky users ↓ 40% over 30 days"). Also feeds Overview trend cards.
- 🔴 **Compliance framework scoring** — map collected signals to **CIS / NIST CSF /
  ISO 27001 / GDPR** controls; show a score per framework + pass/fail per control +
  "how to fix" guidance. (Biggest competitive differentiator.)
- 🔴 **Recommendations layer** — every finding pairs with *why it matters*, *fix steps*,
  and a **deep link** to the right M365 portal blade. Guidance only, no actions.
- 🔴 **Alert coverage gap analysis** — compare the tenant against a best-practice
  alerting baseline and surface **what's NOT being watched**: e.g. no alert on
  privileged-role changes, mailbox forwarding rules, impossible-travel sign-ins,
  MFA-disabled admins, new OAuth app grants, sudden risky-user spikes. Two outputs:
  (1) one-click create the missing **Vigil365 Alert Center** policy from a template
  (in scope — app's own DB), and (2) recommend the missing **native** Defender /
  Entra / Purview alert policy with a **deep link** to create it in M365 (read-only —
  guidance, not an action). Extends the existing 9 alert templates into a coverage
  scorecard ("12 of 20 recommended alerts in place").
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

- 🔴 **Error boundary** — there is **none**; a render error in any component blanks the
  whole app. Add a top-level boundary + per-page fallback.
- 🔴 **Loading skeletons** — currently plain "Loading…" text. Add card/table skeletons
  so the layout doesn't jump.
- 🟡 **Accessibility** — only ~9 aria/role attributes across ~5k lines. Add: aria-labels
  on icon-only buttons, table semantics, focus rings, `aria-live` for toasts, dialog roles.
- 🟡 **Keyboard navigation** — minimal. Esc-to-close all modals, focus trap in modals,
  Enter to submit forms, arrow-key nav in long lists, focus return on close.
- 🟡 **Empty vs error vs no-permission states** — unify into one clear component with
  distinct visuals (no data / failed to load+retry / needs Graph permission).
- 🟡 **Large-list performance** — long tables render all rows; add virtualization or
  server paging for big tenants.
- 🟢 **Favicon + tab branding** — no favicon today; add icon, theme-color meta,
  apple-touch-icon.
- 🟢 **index.html meta** — add description / Open Graph / `<meta name=theme-color>`.
- 🟢 **Consistent number/date formatting** — thousands separators, consistent relative
  vs absolute time, explicit timezone label (UTC vs local) on every timestamp.
- 🟢 **Toasts** — make dismissible, stack, `aria-live=polite`, auto-expire consistently.
- 🟢 **Sticky table headers** on long lists; column min-widths to stop layout shift.
- 🟢 **Per-card "last updated" + manual refresh** affordance.
- 🟢 **Density toggle** (comfortable/compact) for big tenants.
- 🟢 **Mobile pass** — verify every page below 900px (sidebar drawer, table → cards).
- 🟢 **Tooltips/help** — explain each metric (what "risky user" means, thresholds).

## D. Data quality / correctness details

- 🟡 **Timezone clarity** — label timestamps UTC vs local; let user pick.
- 🟡 **Stale-data indicator** — banner when last collection is older than the interval.
- 🟢 **Pagination/total counts** consistent across every list.
- 🟢 **CSV export parity** — ensure every table's export matches the visible/filtered rows.
- 🟢 **Deep-link correctness** — verify each "View in M365 portal" link resolves.

## F. Gaps surfaced later (alerting depth, integrations, edge cases)

**Alert workflow** (core to an alerting product)
- 🔴 Assignment / ownership per alert.
- 🟡 SLA tracking (time-to-ack / time-to-resolve) + escalation if unacked.
- 🟡 Deduplication / correlation — group alerts with the same root cause.
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
- 🟢 In-app version + changelog; opt-in telemetry; i18n; formal WCAG audit.

## E. Enterprise plumbing (tracked, lower priority for this product pass)

- 🔴 **Audit hardening** — capture IP/user-agent, cover sign-in + alert + collection
  events, tamper-evident hash chain, CSV export, retention. (Compliance through-line.)
- 🟡 **/health endpoint** (DB + Graph + last-collection) for monitoring/orchestration.
- 🟡 **Role-claim caching** (short TTL) — stop per-request DB lookups.
- 🟡 **Setup permission verification** — check each required Graph permission is granted
  (fixes ambiguous "Needs permission" on Secure Score).
- 🟢 **Structured logging + correlation IDs**, basic rate limiting.
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
