# Vigil365 — Organization Readiness & Robustness Plan

How we move from a **personal dashboard** (one engineer installs it on their PC)
to a **product an organization/company deploys, trusts, and operates**. This is the
robustness/productization companion to:
- `docs/ENTERPRISE_BACKLOG.md` — features + UI/UX to add
- `ROADMAP.md` — auth + hosting phases

Scope stays: **alerts & visibility, read-only, in-tenant. No remediation.**

Legend: ✅ done · 🔵 WIP · ⬜ to build · 🎯 weekend target

---

## The positioning shift

| Dimension | Personal tool (today) | Organizational product (target) |
|-----------|----------------------|--------------------------------|
| Users | Single engineer | Team with roles (Admin/Analyst/Viewer) ✅ |
| Host | Personal laptop, localhost | Server / VM / container, real hostname ✅ |
| Identity | One person | Microsoft sign-in for the whole tenant ✅ |
| Trust | "It's mine" | Audit trail, encryption, compliance docs 🔵 WIP |
| Lifecycle | Runs until laptop sleeps | Service, monitored, backed up, upgradable ⬜ |
| Data | Throwaway | Retained, owned, recoverable ⬜ |
| Support | The builder | Docs, versioning, disclosure policy ✅/⬜ |

## Robustness pillars

### 1. Reliability ⬜
- 🎯 **Health endpoint** `/health` — DB reachable, Graph creds valid, last-collection age.
- 🎯 **Graph throttling** — handle 429 with backoff/retry (partially present); make robust.
- ⬜ **Collection resilience** — one failing source must never abort the whole run; record
  per-source status (partly there) + surface failures in UI.
- ⬜ **Background worker self-heal** — restart on crash; alert on repeated collection failure.
- ✅ DB-readiness retry on startup (Docker).

### 2. Scalability ⬜
- 🎯 **Role-claim caching** (short TTL) — stop a DB lookup on every request.
- ⬜ **Server-side paging + virtualization** for large tenants (big user/device/alert lists).
- ⬜ **Index review** on hot queries (alerts by date/severity, audit by timestamp ✅).
- ⬜ **Connection pooling / DbContext** tuning for concurrent users.
- ⬜ Document SQL Express 10 GB ceiling → SQL Server / Azure SQL upgrade path ✅(docs).
- ⬜ **Optional SQLite backend** — for lightweight single-org installs with no SQL
  dependency (needs provider-agnostic schema / EF migrations; T-SQL DDL is SQL-Server-specific today).

### 3. Security & trust ✅/🔵 WIP
- ✅ Microsoft sign-in + token validation; RBAC; HTTPS enforcement; encryption at rest
  (cross-platform); SECURITY.md + threat model.
- 🎯 **Audit hardening** — IP/user-agent, sign-in + alert + collection events,
  tamper-evident hash chain, export, retention.
- ⬜ **Certificate auth for Graph** (replace client secret) + secret-rotation flow.
- ⬜ **Seal the Data Protection key ring** (cert / Key Vault) for true at-rest protection.
- 🔴 **Rotate the previously-exposed client secret** before any real org use.
- ⬜ Content-Security-Policy header; basic rate limiting.

### 4. Operability ⬜
- ⬜ **Structured logging** + correlation IDs; log levels; rolling file logs with retention.
- 🎯 `/health` + simple metrics (collection duration, last success, error counts).
- ⬜ **Backup/restore guidance** (DB + Data Protection keys + appsettings).
- ⬜ **Upgrade path** — EF migrations instead of EnsureCreated + raw DDL; documented
  version-to-version upgrade steps.
- ✅ Scripted install/deploy (install.ps1, deploy.ps1, register-app.ps1) + Docker compose.

### 5. Data lifecycle ⬜
- ⬜ **Retention/pruning** — configurable purge of old alerts, collection runs, audit
  (GDPR/SOC2 expectation).
- ⬜ **Metric snapshots** for trends (also a feature) — define what we keep and how long.
- ⬜ **Export** — CSV today; consider scheduled exports / SIEM forward.

### 6. Onboarding & adoption ✅/⬜
- ✅ One-command install + first-run Setup wizard (no JSON editing).
- ✅ Pre-provision users + invite emails.
- ⬜ **Setup permission verification** — confirm each required Graph permission is granted
  (fixes ambiguous "Needs permission").
- ⬜ **In-app "getting started" checklist** — Graph configured? users invited? notifications set?
- ⬜ Sample/demo mode with synthetic data for evaluation without a tenant.

## Deployment models for organizations
- ✅ **Single Windows server** — deploy.ps1 + Windows Service + HTTPS.
- ✅ **Docker / Linux** — docker compose (app + SQL); ⬜ verify on a real Docker host.
- ⬜ **Behind reverse proxy** — Caddy/Nginx/Traefik config + auto Let's Encrypt (public domain).
- ⬜ **HA / scale-out** (future) — stateless app + shared SQL + shared Data Protection keys.

## What an organization needs before trusting it (adoption checklist)
1. ✅ Identity-based access + roles
2. 🔵 WIP Audit trail (who did what) — hardening
3. ✅ Encryption in transit + at rest
4. ⬜ Backup/restore + retention
5. ⬜ Health/monitoring
6. ✅ Security disclosure policy + threat model
7. ⬜ Versioned releases + upgrade docs
8. 🔴 Rotated, vault-stored credentials (no secrets in chat/files)

---

## Weekend build map (suggested sequence)
1. **Foundation/robustness**: error boundary, loading skeletons, unified states,
   `/health`, role caching, audit hardening. *(de-risks everything, builds trust)*
2. **Trends & history** → Overview trend cards → scheduled exec report.
3. **Compliance framework scoring** (CIS/NIST/ISO/GDPR) + **recommendations layer**.
4. **Accessibility + keyboard pass**, CA gap analysis, entity drill-down.
5. **Data lifecycle**: retention/pruning, EF migrations, backup docs.
6. **Polish**: favicon/meta, formatting, tooltips, density, mobile, demo mode.

> Cut line for "organization-ready v1": pillars 1–4 (reliability, scale basics,
> security/trust, operability) + Trends + Compliance scoring. Everything else is
> fast-follow.
