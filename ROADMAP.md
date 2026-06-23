# Vigil365 Roadmap — From Single-User Tool to Org-Ready Product

Vigil365 is moving from a single-engineer, localhost tool to a product an
organization can install on a server, where engineers sign in with their
Entra ID accounts and get role-based access.

This document tracks the work in two parallel tracks plus a maturity track.
Each phase is independently shippable and testable.

---

## Track A — Authentication & Access

### Phase 1 — Secure the API (backend token validation)
- Validate Entra ID Bearer tokens on the backend (not just the UI gate).
- **Root cause of prior 401:** SPA requested scope `api://{clientId}/access_as_user`
  so the token `aud` was `api://{clientId}`, but the backend validated `aud == {clientId}`.
  Fix: set `AzureAd:Audience` to `api://{clientId}` so they match.
- Add `.RequireAuthorization()` per API endpoint (NOT a global FallbackPolicy —
  that broke static file serving). Keep `/api/auth/config` and the SPA fallback
  file as `.AllowAnonymous()`.
- **Exit test:** signed-in user loads data (200); request with no token gets 401.

### Phase 2 — Role-based access (Entra ID App Roles)
- Define App Roles `Admin`, `Analyst`, `Viewer` on the app registration
  (chosen over security groups: self-contained, portable, role claim arrives
  directly in the token).
- Authorization policies:
  - Reads (`GET /api/dashboard/*`, `/api/alerts`, `/api/triggered-alerts`, …) → any authenticated
  - Mutations (`acknowledge`/`resolve`/`snooze`/`unsnooze`, alert-policy CRUD) → Analyst+
  - Settings (`PUT /api/notification-settings`, `POST /api/collector/run`, test) → Admin
- `GET /api/auth/me` returns name, email, roles for the frontend.
- Frontend: role context + `useRole()` hook; hide/disable actions for Viewers,
  settings for non-Admins.
- **Exit test:** Admin sees all; Viewer's mutating calls return 403 and buttons hide.

### Phase 3 — Audit trail & real identity
- New `AuditLog` table (who did what, when) via the existing idempotent schema pattern.
- Replace hardcoded `"dashboard"` in `SnoozedBy`/`AcknowledgedBy` with the real
  UPN from the token (`preferred_username`).
- Surface audit entries in the Alert Center.

### Phase 4 — In-app User Management (Admin only)
- Admin page to list tenant users + assign Admin/Analyst/Viewer in-app.
- Graph write calls via the existing app-only client.
- **Requires optional consent:** `AppRoleAssignment.ReadWrite.All` (write/high-privilege).
  Documented as opt-in — if an org skips it, role management falls back to the
  Azure Portal and the app stays read-only.

---

## Track B — Hosting & Operations

### Phase 5 — HTTPS / TLS + certificate auth  🔴 highest hosting priority
- TLS termination (reverse proxy: IIS / Nginx / Caddy, or Kestrel + cert).
- Switch Graph auth from client secret to **certificate auth** (more secure,
  no rotation window, native PAM-vault support).
- Rotate the previously-exposed client secret.

### Phase 6 — Docker deployment
- `docker-compose.yml` bringing up API + SQL in one command (primary install path).
- Keep IIS / Windows Service path documented for Microsoft shops.

### Phase 7 — Database maturity
- Move from `EnsureCreated()` + raw idempotent SQL to **EF Core Migrations**
  for clean versioned upgrades.
- Document scaling SQL Express → full SQL Server / Azure SQL (10 GB cap).
- Data retention / pruning (alerts, collection runs, audit log).
- Backup / restore guidance.

### Phase 8 — Reliability & observability
- `GET /health` endpoint (DB reachable, Graph creds valid, last collection time).
- Graph throttling (429) retry with backoff.
- Collection partial-failure handling + background worker auto-restart.

---

## Track C — Maturity

### Phase 9 — CI
- GitHub Actions: build + run xUnit tests on every PR (now that community PRs are landing).

### Phase 10 — Polish
- Structured logging + rolling log files with retention.
- API rate limiting.
- Versioning + upgrade docs.
- Concurrency semantics for multiple analysts on the same alert.

---

## Explicitly NOT planned (out of scope)
- Multi-tenant SaaS — single-tenant install only.
- Per-entity alerting (large refactor).
- Maester / third-party tool integration.

---

## Suggested order
Finish **A1 → A2** first (the headline gap everyone is asking about), then
**B5 (HTTPS + cert auth)** to make it genuinely deployable and defensible.
Tracks A and B are independent and can interleave thereafter.
