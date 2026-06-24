# Vigil365 — Project Summary

A self-hosted, open-source Microsoft 365 security dashboard. It aggregates
security posture and alerts from across the Microsoft 365 stack — Defender XDR,
Entra ID, Intune, Exchange Online, and Purview — into a single pane of glass.
All data stays in the customer's own tenant/infrastructure; there is no
third-party SaaS in the data path.

> **Status note:** the public `master` branch reflects the released app. A large
> authentication / multi-user / setup update (sections marked 🆕 below) is on the
> `feature/microsoft-login` branch, validated locally, not yet merged/published.

---

## What it does

| Area | Coverage |
|------|----------|
| Identity | Risky users, risky sign-ins, risk detections, MFA coverage, PIM, foreign sign-ins |
| Devices | Intune compliance, non-compliant + stale devices, Defender endpoint alerts |
| Email | Defender for Office 365 alerts (malware/phish/spam) |
| Incidents | Unified Defender XDR incidents + alerts |
| Compliance | DLP, MCAS, insider-risk, attack simulations |
| Service Health | M365 service advisories |
| Conditional Access | Policy inventory + state |
| Audit / Sign-ins | Unified audit log, geographic sign-in view |
| Alert Center | Custom alert policies, server-side evaluation, notifications (Teams/Email/webhook), per-alert snooze, silent auto-resolve |

## Architecture & stack

| Layer | Technology |
|-------|-----------|
| Backend | ASP.NET Core 8 Minimal API |
| Frontend | React 18 + TypeScript + Vite (SPA) |
| Data source | Microsoft Graph API — app-only (client credentials), **read-only** |
| Scheduler | .NET BackgroundService, 15-minute collection cycle |
| Storage | SQL Server Express by default; scales to SQL Server / Azure SQL (connection-string swap, no code change) |

## Security posture

- **Authentication** 🆕 — Microsoft/Entra ID login (MSAL); backend validates Bearer tokens (audience-scoped).
- **Authorization** 🆕 — role-based access (Admin / Analyst / Viewer). Roles are app-managed (stored in-app), not Entra App Roles; enforced server-side via authorization policies and a claims transformation.
- **User management** 🆕 — in-app admin UI to add/pre-provision, change roles, remove users, and send/resend access-notification emails. Last-admin lockout guards.
- **Audit trail** 🆕 — append-only log of security-relevant actions (user add/role-change/remove/invite, settings, setup), with actor identity from the validated token.
- **Encryption at rest** — secrets (SMTP password, webhook URLs, Graph client secret) DPAPI-encrypted; SQL connection uses `Encrypt=True`.
- **Encryption in transit** 🆕 — HSTS + HTTPS redirection enforced outside Development; TLS via reverse proxy or Kestrel certificate (documented).
- **Least privilege** — Graph permissions are all `*.Read.All`; the app never writes to the tenant.
- **Network model** — designed as an internal/self-hosted tool; not a public multi-tenant SaaS.

## Install model 🆕

Reduced to ~3 steps:
1. `install.ps1` — checks prerequisites, builds the frontend, publishes the API, optional Windows-service install.
2. One-time Entra app registration (app-only Graph permissions + SPA redirect URI + `access_as_user` scope).
3. Sign in (first user becomes Admin) → **in-app first-run setup wizard** to enter Graph credentials (stored encrypted; no JSON editing).

## Roadmap

**Recently landed (local branch):** Microsoft login, RBAC, in-app user management + invites, audit trail, production HTTPS enforcement, one-shot installer + first-run setup wizard.

**In progress / planned:**
- Trends & history page — metric snapshots over time (risky users, compliance, secure score) with exec-friendly up/down/flat readouts.
- Certificate-based Graph auth (replacing client secret) + secret-vault integration.
- Data retention / pruning policies.
- `register-app.ps1` to script the Entra app registration.
- Optional SQLite backend to drop the SQL Server dependency.
- Docker / docker-compose deployment.
- CI with automated tests; dependency scanning.

## Scope & honest limitations

- Not a SIEM — no raw-log ingestion, KQL hunting, or SOAR. It complements tools like Microsoft Sentinel rather than replacing them.
- Visibility is bounded by what Microsoft Graph exposes.
- Single-tenant, self-hosted by design.
- Historical trends accrue from when snapshotting begins (no retroactive history).

## Links

- Repository: https://github.com/sameerk27/vigil365
- License: MIT
