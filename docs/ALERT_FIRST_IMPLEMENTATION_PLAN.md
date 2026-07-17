# Vigil365 Alert-First Implementation Plan

## Product decision

Vigil365 is a **single-tenant Microsoft 365 security-alerting and investigation
tool**. It brings the important signals into one queue, helps an analyst decide
what matters, records the decision, and notifies the right people.

It is deliberately **not** an AdminDroid-style reporting catalog, a tenant
management console, a remediation engine, or a multi-tenant MSP platform.

**North-star workflow:**

```text
Collect signal -> create/dedupe alert -> prioritise -> investigate -> decide
-> notify/escalate -> retain an auditable record
```

Every roadmap item must make one of those steps faster, clearer, or more
trustworthy. If it mainly adds dashboard breadth, report count, or write access
to Microsoft 365, it is out of scope.

## Starting point (already delivered)

- Entra authentication, role-based access, audit trail, alert policies, activity
  and anomaly alerting, analyst notes/assignment, notifications, activity feed,
  trends, retention, health checks, Graph retry handling, certificate Graph auth,
  EF migrations, Docker/Windows deployment paths, and CI.
- The current product risk is not missing dashboards; it is operational polish and
  making the alert journey feel effortless under real incident pressure.

## Release 1 — Trust the queue

**Goal:** An analyst can open Vigil365 and immediately know what needs attention
and whether the collector is trustworthy.

### 1. Collection health in the alert UI

- Display last successful collection time, duration, source failures, and next
  scheduled run in the alert queue header.
- Add a compact degraded banner when collection is stale or partially failing;
  link to source-level failure details.
- Provide a read-only collection-run history with filtering and copyable error
  details.

**Acceptance:** A user never mistakes stale data for a quiet tenant, and can
identify a failed Graph source without reading server logs.

### 2. Incident queue ergonomics

- Establish a single default queue: open alerts, severity-first, with clear
  `New`, `Acknowledged`, `Snoozed`, `Resolved`, and `Escalated` states.
- Make the highest-value filters persistent: severity, policy, owner, age,
  status, and affected entity.
- Save named investigation views locally first; add shared views only after the
  model and permissions are proven.
- Keep bulk acknowledge/resolve, undo, keyboard focus, and deep links consistent
  across desktop layouts.

**Acceptance:** A triage analyst can move from login to a filtered, shareable
alert list in under 30 seconds.

### 3. Operational logging and recovery

- Add rolling, structured application logs with configurable retention and a
  documented log location for Windows and Docker deployments.
- Include correlation ID, actor, alert ID/policy ID, collection-run ID, and
  outcome in relevant log events; never log secrets or raw access tokens.
- Publish backup/restore and upgrade runbooks covering SQL data, Data Protection
  keys, and app configuration.

**Acceptance:** An operator can investigate a failed run and restore a test
instance using only the runbook.

## Release 2 — Investigate without leaving the app

**Goal:** An analyst can understand an alert's evidence and make a defensible
decision in one place.

### 4. Unified alert evidence timeline

- Turn the alert detail panel into a chronological timeline: trigger context,
  related audit events, prior alerts for the same entity/policy, notes,
  assignments, notifications, and state changes.
- Make the evidence source explicit (`Graph alert`, `audit event`, `trend`, or
  `derived metric`) and retain a safe link to the relevant Microsoft portal.
- Add a concise analyst decision field: `true positive`, `benign`, `expected
  change`, or `needs escalation`, with a required note for closure.

**Acceptance:** A different analyst can explain why an alert was closed from the
record alone.

### 5. Entity-centred investigation

- Add a focused entity page for users and devices, with recent alerts, activity,
  risk indicators, ownership, and portal links.
- Keep data bounded by an explicit time range and page it server-side.
- Link entity names consistently from the queue, detail panel, and activity feed.

**Acceptance:** Investigating a user/device requires no manual cross-page search.

### 6. Escalation-quality notifications

- Send direct alert links, severity, affected entity, trigger evidence, current
  owner, and a compact decision/status summary in email, Teams, and webhook
  notifications.
- Add digest mode for low-severity notifications and a visible delivery-failure
  alert for every configured channel.

**Acceptance:** A notification lets a recipient judge urgency and reach the
exact record in one click.

## Release 3 — Expand only high-value security coverage

**Goal:** Add the signals a security team expects, without becoming a generic
reporting platform.

### 7. Coverage packs, not report packs

- Ship curated, versioned alert packs for identity privilege change, mailbox
  forwarding, risky sign-ins, OAuth/app consent, Conditional Access changes, and
  external sharing.
- Show a coverage scorecard that says which recommended detections are enabled,
  unsupported, or missing permissions.
- Add a policy preview against retained audit events before enabling a policy to
  control noise.

**Acceptance:** An administrator can enable a relevant security baseline in
minutes and understand expected alert volume before doing so.

### 8. Conditional Access and sharing risk

- Prioritise CA gaps (MFA exclusions, legacy authentication exposure, uncovered
  privileged users) and SharePoint/OneDrive external-sharing signals.
- Present each as an alertable risk with evidence and a Microsoft portal deep
  link, never as an in-app remediation action.

**Acceptance:** The product surfaces the highest-impact configuration gaps as
actionable security findings, not isolated dashboards.

## Continuous quality gates

- Unit/integration coverage for policy evaluation, state transitions, access
  control, deduplication, notification delivery, and retention.
- Browser-level smoke tests for login, queue filtering, alert acknowledgement,
  investigation notes, and a degraded-collection banner.
- Accessibility checks: keyboard-only queue/detail use, focus management,
  contrast, labels, and screen-reader announcements.
- Performance budgets: paged API responses and no unbounded table rendering.
- Security gates: dependency scanning, SBOM/release artifact, secret scanning,
  and a credential rotation checklist.

## Explicit non-goals

- M365 administration, write/remediation actions, bulk management, and rollback.
- Thousands of generic reports or a report-builder arms race.
- Multi-tenant/MSP hosting, partner branding, or cross-tenant data aggregation.
- Replacing a SIEM/SOAR; Vigil365 should link and export cleanly when those tools
  are present.

## Sequencing

1. Release 1: collector trust, queue usability, logging/recovery.
2. Release 2: alert evidence, entity investigation, escalation quality.
3. Release 3: curated detection packs and the two highest-value coverage areas.

Do not start Release 3 until Release 1's queue and reliability acceptance tests
pass. Better detection volume is harmful if the team cannot trust or process the
alerts it already has.
