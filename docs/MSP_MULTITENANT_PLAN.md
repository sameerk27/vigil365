# Vigil365 — MSP Multi-Tenant Alerting Plan

Take Vigil365 from **single-tenant** (one org, its own data) to **MSP multi-tenant**:
one deployment an MSP runs to monitor and alert across **many client M365 tenants**
from a single pane, with strict per-client isolation.

> ⚠️ This is the single largest architectural change to the product. It touches the
> data model, collection pipeline, authentication, access control, UI, and — most
> critically — the security/isolation model. Treat it as a major version (v2), not
> a feature. Scope stays **read-only, alerts/visibility only**.

---

## 0. The positioning shift (and the honest trade-off)

| | Single-tenant (today) | MSP multi-tenant (target) |
|---|---|---|
| Data location | Customer's own tenant/host | **MSP's** host, holding *many* customers' security data |
| Trust model | "Your data never leaves your tenant" | MSP is now a data processor for N customers |
| Blast radius | One tenant | **All client tenants** if isolation fails |
| Buyer | IT team | MSP / MSSP |

**Be explicit about this in marketing.** The "data stays in your tenant" line no
longer applies for the MSP edition — the MSP custodies client data. That demands a
DPA, strong isolation, and per-client audit. Keep the single-tenant edition as-is
for direct customers; multi-tenant is a separate deployment mode.

---

## 1. Connecting to client tenants (the Microsoft side)

- **Multi-tenant app registration** — the MSP registers ONE app, marked
  multi-tenant. Each client admin grants **admin consent** in their tenant
  (`https://login.microsoftonline.com/{clientTenantId}/adminconsent?client_id=...`).
- **GDAP (Granular Delegated Admin Privileges)** — the modern MSP delegation model
  (replaces legacy DAP). Pair the app with least-privilege GDAP roles
  (Security Reader / Global Reader) so the MSP gets scoped, time-bound read access.
- **Per-tenant app-only tokens** — acquire tokens per client tenant against the
  client's authority (`/{clientTenantId}`). The MSP app's secret/cert is one;
  the *token audience/authority* is per client.
- **Onboarding flow** — an admin "Add client tenant" wizard: enter/clientconsent →
  record `{ TenantId, DisplayName, ConsentStatus, ConnectedAt }` → first collection.

## 2. Data isolation (the make-or-break decision)

**Recommended: single database + `TenantId` on every row + a mandatory EF Core
global query filter.**

- Add `TenantId` (Guid/string) to **every** collected entity: SecurityAlerts,
  CollectionRuns, TriggeredAlerts, TrendSnapshots, AuditEntries, NotificationLogs, etc.
- Enforce `modelBuilder.Entity<T>().HasQueryFilter(e => e.TenantId == _current.TenantId)`
  via an injected "current tenant" accessor — so a forgotten `.Where` can't leak
  cross-tenant data. **This is the single most important safety control.**
- A `ClientTenant` table holds connection metadata + display name + consent status.
- **Alternative (stronger, heavier): database-per-tenant** — best isolation, far more
  ops (migrations × N, backup × N). Offer as an enterprise option; default to
  row-level for manageability.

> **Mandatory:** isolation tests. For every read path, a test proving tenant A
> cannot see tenant B's rows. A cross-tenant leak in a security product is fatal.

## 3. Collection pipeline changes

- The background worker loops **all connected client tenants**: per tenant →
  acquire token → run the existing collectors → **tag every row with `TenantId`**.
- **Scale/throttling:** stagger tenants, cap parallelism, handle Graph 429 per
  tenant independently, and record per-tenant collection status + last-success.
- **Resilience:** one tenant failing (consent revoked, throttled) must not stop the
  others. Per-tenant `CollectionRun` rows already fit this.
- Secure Score / MFA-coverage live calls become per-tenant too.

## 4. Access control for MSP staff

- Extend roles: **MSP-Admin** (all tenants + settings), **Analyst** (act on alerts),
  **Viewer** (read) — plus **tenant scoping**: an engineer can be limited to a subset
  of client tenants (`AppUser` ↔ allowed `TenantId`s mapping).
- The current in-app user model + claims transformation extend naturally; add a
  per-user tenant-allowlist and enforce it alongside the global query filter.

## 5. UI changes

- **Tenant switcher** in the header: "All clients" aggregated view + per-client drill-down.
- **Cross-tenant alert feed** — one prioritized stream across all clients, each row
  tagged with the client name + severity.
- **Per-client dashboards** — the existing pages, scoped to the selected tenant.
- **Client roster page** — connection/consent health per tenant, last collection,
  alert counts.
- **White-label / branding** per client for reports (MSP logo + client name).

## 6. Alerting & notifications

- Alert policies: **global templates** applied across tenants + **per-tenant overrides**.
- Notification routing **per client** (different Teams/email/webhook per tenant, or
  central MSP SOC inbox) — extend `NotificationSettings` to be per-tenant.
- Cross-tenant **digest** ("12 clients, 3 with critical alerts this week").

## 7. Security & compliance (raised stakes)

- **Isolation tests** (see §2) — non-negotiable.
- **Per-tenant audit trail** — `AuditEntry.TenantId`; MSP-Admin actions scoped + logged.
- **GDAP least privilege + time-bound** access; surface consent/role expiry.
- **Encryption at rest** already in place (Data Protection) — keep; consider per-tenant
  key separation for the strongest posture.
- **Bigger blast radius** → certificate auth for the MSP app (not a shared secret),
  vault-stored, and rotation. (Pulls forward the cert-auth backlog item.)
- DPA + data-handling docs for the MSP-as-processor model.

## 8. Phased rollout (each phase shippable)

1. **Data model** — add `TenantId` everywhere + `ClientTenant` table + EF global
   query filter + isolation tests. (Foundation; no UI yet.)
2. **Connection & collection** — multi-tenant app reg, client onboarding wizard,
   per-tenant token acquisition, collector loop tagging by tenant.
3. **Access & UI** — tenant scoping for staff, tenant switcher, client roster,
   aggregated + per-client views.
4. **Alerting** — per-tenant policies/routing, cross-tenant feed + digest.
5. **Hardening** — cert auth for the MSP app, per-tenant audit, white-label reports,
   scale tuning, DPA docs.

## 9. Risks & explicit non-goals

- **Risk:** cross-tenant data leakage — mitigated by global query filter + tests; the
  highest-priority correctness concern.
- **Risk:** scale (many tenants × frequent collection) — mitigated by staggering,
  parallelism caps, per-tenant backoff.
- **Risk:** consent/GDAP churn — surface connection health, fail gracefully per tenant.
- **Non-goals (unchanged):** remediation actions, raw-log SIEM ingestion. Still
  read-only alerting.
- **Keep both editions:** single-tenant (in-tenant, "data stays put") and MSP
  multi-tenant (MSP-custodied) are different trust models — ship and message separately.

## 10. Effort (honest)

This is a **multi-week v2**, not a weekend. Phase 1 alone (TenantId + query filter +
isolation tests across the whole data model and every query) is significant and must
be done meticulously — it's the safety foundation everything else rests on.
