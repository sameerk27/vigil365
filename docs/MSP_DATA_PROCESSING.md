# Vigil365 MSP Edition — Data Processing and Handling

This document is written for the MSP operating Vigil365 and for the MSP's clients'
data-protection reviewers. It describes exactly what Vigil365 stores about each client
tenant, where, for how long, who can see it, and how it is removed. It is a factual
description of the software's behaviour, intended to be attached to (not to replace)
the MSP's own Data Processing Agreement with each client.

**Roles.** The client organisation is the *controller* of its Microsoft 365 data. The
MSP running Vigil365 is a *processor* acting on the client's instructions (the admin
consent the client grants). Microsoft remains the client's processor for Microsoft 365
itself; Vigil365 reads from Microsoft Graph and never writes back.

---

## 1. What is collected, per client

Vigil365 is read-only monitoring. Through Microsoft Graph, using application
permissions the client's Global Administrator consented to, it collects:

| Category | Examples of fields stored | Purpose |
|---|---|---|
| Security alerts (Defender XDR, Entra ID Protection) | alert id, type, severity, title, description, affected user principal name or device name, timestamps, raw alert JSON | Alerting and triage |
| Directory audit events | Graph record id, activity name, category, initiating user, target, timestamp, raw event JSON | Activity-based alert policies |
| Posture metrics (snapshots) | counts: risky users, MFA coverage %, non-compliant devices, Secure Score %, open critical/high alerts | Trends and drift |
| Collection metadata | run timestamps, status, Graph request/throttle counts, source failure details | Operations |

Vigil365 does **not** collect mailbox content, file content, message bodies, or
credentials of the client's users. It does not perform remediation of any kind.

Full list of Graph permissions requested: `docs/graph-permissions.md`.

## 2. Where it is stored and how it is separated

All client data lives in one relational database (SQL Server or PostgreSQL) operated
by the MSP. Every row that belongs to a client carries that client's tenant id, and:

- **Reads** are confined to one client at a time by a database-level global query
  filter that the application cannot bypass without an explicit, audited,
  code-reviewed cross-tenant read (used only for the MSP's own rollup views and the
  audit log's integrity chain).
- **Writes** are stamped with the active client and refused if they name any other.
- **No client selected** means an error, never "all clients".
- These guarantees are verified by an automated isolation test suite that runs against
  real SQL Server and real PostgreSQL on every build (`src/M365SecurityDashboard.Api.Tests/Relational/TenantIsolationTests.cs`).

Client-specific secrets (the client's app-registration secret or certificate
password, per-client webhook URLs) are encrypted at rest with ASP.NET Core Data
Protection keys held by the MSP's installation.

By default every client consents to the MSP's **one shared app registration**. Its
client secret is in the installation's `appsettings.Production.json`, readable only
by Administrators and the service account (or, if entered on the Setup page,
encrypted in the database). Whoever holds it has the permissions in §8 in every
consenting client's tenant, so the MSP must protect it accordingly.

## 3. Who can see it

- **MSP Admins** see every client.
- **Other MSP staff** see only the clients an Admin has explicitly assigned to them.
  A new staff member sees no client until assigned.
- **Clients** have no login to Vigil365. Sign-in is pinned to the MSP's own Entra
  tenant; a user from a client tenant is refused even if they hold a valid token.
  Clients receive what the MSP routes to them (alert emails, Teams/webhook messages,
  scheduled reports) and nothing else.
- Every administrative action (assigning staff, changing credentials, deactivating or
  purging a client, changing routing) is written to a tamper-evident audit log
  (SHA-256 hash chain, verifiable and exportable). Each entry records the acting MSP
  user, the action, the client it concerned (none for MSP-level actions), the source
  IP address and up to 500 characters of detail.

## 4. Where it is sent

Alerts and digests are sent only to the destinations configured by the MSP:

- the MSP's own channels (email, Teams, webhook), and/or
- the client's own channels, if the MSP configures per-client routing.

Vigil365 makes no other outbound connections with client data. It contacts Microsoft
Graph and the Microsoft login service to collect, and the configured SMTP server /
webhook endpoints to notify. There is no telemetry to the Vigil365 project.

## 5. Retention

Data is pruned automatically per client on a nightly schedule. Defaults (configurable
by the MSP in `Retention` settings):

| Data | Default retention |
|---|---|
| Resolved security alerts | 90 days (open alerts are never pruned) |
| Resolved triggered alerts | 180 days |
| Directory audit events | 90 days |
| Notification delivery log | 90 days |
| Collection run history | 90 days |
| Posture trend snapshots | 365 days |
| Vigil365 audit log | 365 days (one chain for the whole install, pruned oldest first once a day) |

Backups of the database are the MSP's responsibility and inherit the database's
retention; the MSP should align backup retention with the DPA.

## 6. Offboarding and deletion

Two operations exist, both Admin-only and audited:

- **Deactivate** — collection stops, the client disappears from staff views, data is
  kept (for a contractual retention period, or pending deletion).
- **Purge** — the client record is deleted and every row it owns is deleted with it by
  database cascade: alerts, directory audit events, runs, snapshots, notes,
  suppression rules, routing, policy overrides, staff assignments, client-restricted
  API tokens. The isolation test suite includes an offboarding test proving none of
  that remains and nothing of any other client is touched.
  **Kept:** the Vigil365 audit log entries about the client (§3), including the
  purge itself. They are part of the tamper-evident chain, which would break if they
  were removed. They age out with the audit log retention (§5).

Purge does not rewrite database backups; those expire on the backup schedule.

## 7. Sub-processors

Vigil365 itself introduces none. The MSP's sub-processors are the MSP's hosting
provider for the Vigil365 server and database, and whatever email/Teams/webhook
services the MSP configures for notifications.

## 8. Security measures (summary)

- Application permissions only; certificate authentication supported per client;
  secrets encrypted at rest; TLS enforced outside development.
- Least privilege for staff via per-client assignment; role-based access
  (Admin/Analyst/Viewer) on top.
- Tamper-evident audit log; session limits (idle and absolute) in the web UI.
- Read-only by design, with one exception the client should know about: every
  permission requested is read-only except `AttackSimulation.ReadWrite.All`, because
  Microsoft Graph offers no read-only permission for attack-simulation results. It
  allows creating and launching phishing simulations in the client's tenant.
  Vigil365 only reads with it. A client that does not accept it can revoke that one
  permission after consenting; the attack-simulation results are then missing.
  Full list: `docs/graph-permissions.md`.
- Threat model: `docs/THREAT_MODEL.md`. Operations: `docs/OPERATIONS_RUNBOOK.md`.

## 9. Incident handling

If the MSP suspects unauthorised access to Vigil365 or its database, the runbook
steps are: rotate the Data Protection key ring and all client app secrets/certificates
(each client's credentials can be replaced independently via the tenants API), review
the audit log export, and notify affected clients per the DPA. Vigil365 never writes
to a client tenant, so a compromise of Vigil365 itself exposes the collected data
described in §1. A stolen app secret is wider: it grants the consented permissions
directly, including creating attack simulations in each consenting client's tenant
(§8). Rotate it first.
