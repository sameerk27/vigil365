# Vigil365 — Implementation Plan (compiled July 2026)

Compiled from: two engineering audits, the senior-designer detail audit, the veteran
SecOps critique, the card-level QA pass, and competitive research against
**AdminDroid** and **ManageEngine M365 Manager Plus**.

**Product goal (north star):** *"AdminDroid tells your IT admin what happened in
M365; Vigil365 tells your security team what's wrong — with the audit trail to
prove nobody touched anything."* Security alerting & visibility, self-hosted,
strictly read-only against the tenant. We chase security depth, never their
1,800-report breadth or management actions.

Effort: **S** ≤ ½ day · **M** 1–2 days · **L** 3+ days.

---

## Phase 0 — Close the ship gate *(release blocker)*

| # | Item | Effort | Notes |
|---|------|--------|-------|
| 0.1 | **Certificate auth for Graph** | M | Optional cert (thumbprint or PFX path) with fallback to client secret; setup wizard support; docs. Marketing line: "no client secrets stored anywhere". |
| 0.2 | **EF Core migrations** | M | Replace `EnsureCreated` + raw DDL with real migrations; baseline migration matching current schema; upgrade path for existing installs. |
| 0.3 | **Rotate exposed client secret** | — | **Owner action in Entra.** Blocks any public/prod use. |

**Exit criteria:** fresh install and upgrade-from-current both work; Graph runs on a certificate; backlog "Required to ship" list is empty.

---

## Phase 1 — Alert workbench *(viewer → tool people work in)*

The category-wide weakness (competitors included). All local-DB; read-only promise intact.

| # | Item | Effort | Notes |
|---|------|--------|-------|
| 1.1 | **Table toolkit**: sortable headers, server pagination ("1–50 of N" footer), bulk select + bulk ack/resolve | M | One shared component; Alert Queue + Alert Center first, then all tables. Backend: paging envelope (`total/page/items`) on triggered-alerts + alerts. |
| 1.2 | **Right slide-in detail panel** replacing centered alert modals | M | Category convention (Defender/Sentinel). Keep queue visible; prev/next navigation between alerts; reuse permalink sync. |
| 1.3 | **Assignment + analyst notes + local disposition** (reviewed / escalated / false-positive) on every alert incl. M365 ones | M | New columns + endpoints + audit events; shown in panel and queue. |
| 1.4 | **SLA age**: time-since-triggered / time-to-ack visible on queue rows; overdue highlight | S | Pure frontend from existing timestamps + one threshold setting. |
| 1.5 | **Permalinks in notifications** | S | Teams/email/webhook templates link `#/incidents?alert={id}` — routing already supports it. |

**Exit criteria:** an analyst can sort the queue worst-first, bulk-ack noise, open a
side panel, assign to a teammate with a note, and click a Teams message straight to
the alert.

---

## Phase 2 — Findability & layout standardisation

| # | Item | Effort | Notes |
|---|------|--------|-------|
| 2.1 | **Global search in header (Ctrl+K)** across users / devices / alerts / pages | M | Client-side over already-loaded data first; the missing anchor component. |
| 2.2 | **One standard filter toolbar** (search → selects → date range → clear → export) same position on every list page; fix Email's hidden cross-filtering search | M | Extract `<FilterToolbar>`; Email + Devices brought into pattern. |
| 2.3 | **Sticky section tabs** (travel with header) + restyle Alert Center inner tabs as underline style | S | Kills the double-pill-bar confusion. |
| 2.4 | **Undo toasts** (ack/resolve), **per-card "updated Xm ago"**, pause-auto-refresh control | M | Trust + control trio from the UX review. |
| 2.5 | **Metric tooltips + first-run checklist** ("3 steps to your first alert") | M | Onboarding + comprehension; kills the empty-dash first impression. |
| 2.6 | Time-range control on Alerts/Identity + timezone label on timestamps | M | |
| 2.7 | Card-slot hygiene: Notification Settings toggles out of badge slot; hover affordance distinguishing clickable KPI tiles | S | |

---

## Phase 3 — Competitive core: activity alerting + reports

The architectural gap vs AdminDroid (1,400 alertable activities vs our ~10 metric policies).

| # | Item | Effort | Notes |
|---|------|--------|-------|
| 3.1 | **Audit-activity alerting engine** | L | New collector source: unified audit log / directory audit events → normalized `AuditEvent` store; new policy type `activity-match` (operation, actor, target, count-in-window); starter pack of ~25 security-relevant activity policies (forwarding rule created, role assignment, mass download, CA policy changed…). The coverage scorecard already *recommends* these — now they fire. |
| 3.2 | **Anomaly / comparison alerts** | M | "Metric ≥ N× its 30-day baseline" policy type on TrendSnapshots (AdminDroid's cleverest feature, cheap for us). |
| 3.3 | **Report library + scheduling** | L | 40–50 curated *security* reports (not admin breadth), schedulable email PDF/CSV; includes the **weekly exec digest** (posture + trends + top alerts). Reuses SMTP + Trends. |
| 3.4 | **Notification digest mode + delivery-failure alert** | S | Daily rollup option per channel; alert when a channel fails. |

**Exit criteria:** "mailbox forwarding rule created" fires an alert within one
collection cycle; a CISO gets a Monday-morning PDF without asking.

---

## Phase 4 — Investigation depth

| # | Item | Effort | Notes |
|---|------|--------|-------|
| 4.1 | **Entity drill-down page** (`#/entity/{upn|device}`): timeline of alerts, sign-ins, risk history, devices | L | The dashboard→investigation-tool jump; data already collected. |
| 4.2 | **Incident ↔ alert join** via existing `incidentId` | S | Incident panel lists member alerts; alert panel links its incident. |
| 4.3 | CA gap analysis (users/apps not covered, MFA-exempt, legacy auth) | M | Existing backlog ask. |
| 4.4 | SharePoint/OneDrive sharing signals (new collector source) | M | Most-requested coverage extension. |

---

## Phase 5 — Platform & product maturity *(parallelizable, lower urgency)*

- **TanStack Query migration** (kills 26-useState god component, double-fetch class, per-page fetching) — L
- **Split 1,800-line Program.cs** into endpoint groups — M
- **List virtualization** for big tenants — M
- **Trends page re-skin** onto the design system (last off-system page) — M
- Modal focus-trap parity (PolicyModal, triggered-alert modal) + keyboard access on list rows — M
- Server-side-only policy evaluation (drop client-triggered evaluate) — S
- Mobile drawer nav (or commit to desktop-only formally) — L
- **Sovereign cloud endpoints** (GCC/GCC-High/21Vianet configurable) — M
- **Release machinery**: versioned releases + changelog + docs site + SBOM/signed releases + dependency scanning — M
- Idle timeout / auto sign-out — S

---

## Sequencing & rationale

```
Phase 0  ─ ship gate           (must finish first — everything else is v1.x)
Phase 1  ─ alert workbench     (biggest daily-use gap; category-wide weakness = differentiation)
Phase 2  ─ findability/layout  (cheap, high-visibility; can interleave with Phase 1)
Phase 3  ─ activity alerting   (the strategic competitive bet; largest single build)
Phase 4  ─ investigation depth (builds on 1's panel + 3's events)
Phase 5  ─ platform maturity   (continuous background track)
```

Rough calendar at current pace: Phase 0+1 ≈ one week · Phase 2 ≈ 2–3 days ·
Phase 3 ≈ 1–2 weeks · Phase 4 ≈ one week. **Working sellable milestone after
Phase 3** — that's when the AdminDroid comparison stops losing.

## Explicitly out of scope (unchanged)
Remediation actions · multi-tenant SaaS (Edition 2) · raw-log SIEM ingestion ·
management/delegation features (M365 Manager Plus's turf) · report-count arms race.
