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

## 3. Who can see it

- **MSP Admins** see every client.
- **Other MSP staff** see only the clients an Admin has explicitly assigned to them.
  A new staff member sees no client until assigned.
- **Clients** have no login to Vigil365 unless the MSP chooses to grant one, in which
  case the same assignment mechanism limits them to their own tenant.
- Every administrative action (assigning staff, changing credentials, deactivating or
  purging a client, changing routing) is written to a tamper-evident audit log
  (SHA-256 hash chain, verifiable and exportable).

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
| Vigil365 audit log | 365 days |

Backups of the database are the MSP's responsibility and inherit the database's
retention; the MSP should align backup retention with the DPA.

## 6. Offboarding and deletion

Two operations exist, both Admin-only and audited:

- **Deactivate** — collection stops, the client disappears from staff views, data is
  kept (for a contractual retention period, or pending deletion).
- **Purge** — the client record is deleted and every row it owns is deleted with it by
  database cascade: alerts, audit events, runs, snapshots, notes, suppression rules,
  routing, policy overrides, staff assignments, client-restricted API tokens. The
  isolation test suite includes an offboarding test proving nothing of the client
  remains and nothing of any other client is touched.

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
- Read-only by design: no permission that can modify the client's tenant is requested.
- Threat model: `docs/THREAT_MODEL.md`. Operations: `docs/OPERATIONS_RUNBOOK.md`.

## 9. Incident handling

If the MSP suspects unauthorised access to Vigil365 or its database, the runbook
steps are: rotate the Data Protection key ring and all client app secrets/certificates
(each client's credentials can be replaced independently via the tenants API), review
the audit log export, and notify affected clients per the DPA. Because Vigil365 is
read-only, a compromise exposes the collected data described in §1 but cannot alter
any client tenant.
