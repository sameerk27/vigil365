# Vigil365 — Threat Model

A concise threat model covering trust boundaries, data flows, assets, and the
controls that protect them. Scope is the self-hosted, single-tenant deployment
described in `SECURITY.md`.

## Assets

| Asset | Sensitivity |
|-------|-------------|
| Graph API credentials (tenant/client ID, client secret/cert) | **Critical** — grant read access to tenant security data |
| Collected M365 security data (alerts, risky users, devices, sign-ins) | High — reveals security posture |
| Notification secrets (SMTP password, webhook URLs) | High |
| User roles / access assignments | Medium |
| Audit trail | Medium — integrity matters for accountability |

## Trust boundaries

```
[ Operator's browser ] --HTTPS--> [ Reverse proxy / Kestrel TLS ]
                                          |
                                   [ Vigil365 API ]  --app-only token-->  [ Microsoft Graph ]
                                          |
                                   [ SQL database (in-tenant) ]
```

1. **Browser ↔ App** — authenticated user session; should always be HTTPS in
   production. Untrusted input crosses here. The browser talks only to Vigil365 and
   Microsoft sign-in: CSP `connect-src` is `'self'`, the sign-in authority and `wss:`.
   Every Microsoft API is called by the server, never by the browser.
2. **App ↔ Microsoft Graph** — outbound, app-only OAuth2; read-only scopes except
   `AttackSimulation.ReadWrite.All` (no read-only variant exists; the app only reads).
   The only POST starts a Purview audit search (optional `AuditLogsQuery.Read.All`),
   enforced by an allow-list in the Graph client and a test. It goes only to
   `/security/auditLog/queries`, which creates a search job and changes nothing in the
   tenant. There are three searches per tenant, all to that path with that permission:
   Copilot Studio by operation name, Power Platform by record type, and Power Platform
   admin changes by operation name (Graph's record type list has no member for them). Optional
   `InformationProtectionPolicy.Read.All` reads sensitivity label names and settings only
   (no labelled content).
3. **App ↔ Database** — trusted, same-network; transport-encrypted.
4. **App ↔ SMTP/webhooks** — outbound notifications; secrets decrypted in memory
   only at send time.

## Data flows

- The background collector pulls current-state security data from Graph every
  ~15 minutes and persists it to SQL.
- The browser SPA reads that data through the API.
- The Purview audit collector (hourly by default) starts the three audit searches above
  and, on a later run, pages their records into `AuditEvents`; activity rules alert on them.
  The Power Platform page shows those events and alerts from Vigil365's own API. It has no
  Microsoft sign-in of its own and Vigil365's app has no Power Platform API access.
- Privileged/mutating actions (acknowledge, snooze, settings, user management)
  are authenticated and, where applicable, role-gated server-side.
- Licence posture (`/api/posture`, `/api/posture/report.html`) reads licences and
  configuration from Graph with GETs only. It writes nothing to the database. The result is held
  in server memory for 5 minutes, keyed by tenant id so one client's posture is never served to
  another. Any signed-in
  user (Viewer and up) can read it and download the client report, which names Conditional
  Access policies and the tenant's licences. Every tenant value in the report is HTML-encoded,
  and the MSP's brand colour is accepted only as `#RRGGBB`.

## Threats & mitigations (STRIDE-aligned)

| Threat | Mitigation |
|--------|-----------|
| **Spoofing** — unauthorised access to the dashboard | Entra ID sign-in (in progress); network isolation; tenant-scoped login |
| **Tampering** — modifying data/config | Server-side authorization; read-only Graph (no tenant writes); append-only audit trail |
| **Repudiation** — denying an action | Audit entries capture actor identity from the validated token, and the client IP: behind a reverse proxy, `X-Forwarded-For` is believed only from proxies listed in `ForwardedHeaders:KnownProxies`/`KnownNetworks` (loopback by default), so a caller cannot forge it |
| **Information disclosure** — leaking secrets or data | Secrets in the database encrypted with ASP.NET Core Data Protection (key ring protected by file permissions); config file with the Graph secret readable only by Administrators and the service; SMTP password and credentials never returned by the API; generic error messages; data stays in-tenant; HTTPS in production |
| **Denial of service** | Self-hosted/internal exposure limits blast radius; Graph 429 handling with backoff |
| **Elevation of privilege** | Role policies enforced on the server, not just hidden in the UI; last-admin lockout guards |

## Out of scope / assumptions

- The host OS, SQL Server, and network are administered and patched by the operator.
- The Entra app registration is correctly configured with least-privilege
  permissions (read-only except `AttackSimulation.ReadWrite.All`), and is not enrolled
  as a Power Platform management application.
- Vigil365 is **not** a SIEM and does not ingest raw logs; visibility is bounded
  by what Microsoft Graph exposes.
- Physical security and tenant-admin trust are assumed.

## Residual risks

- A compromised Graph credential exposes read access to tenant security data, and
  can create attack simulations (in MSP mode, in every consenting client's tenant) —
  hence credential storage in a vault and rotation are operator responsibilities.
- Copilot and Power Platform alerts depend on Microsoft's audit log. Records can take up
  to 24 hours to arrive, and that Graph returns Power Platform admin records when asked by
  operation name is not yet checked on a live tenant. A missing record means a missed
  alert, not wrong data.
- Until identity sign-in is merged to the released branch, network isolation is
  the primary access control for the public release.
