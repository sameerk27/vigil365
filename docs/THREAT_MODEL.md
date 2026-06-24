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
   production. Untrusted input crosses here.
2. **App ↔ Microsoft Graph** — outbound, app-only OAuth2; read-only scopes.
3. **App ↔ Database** — trusted, same-network; transport-encrypted.
4. **App ↔ SMTP/webhooks** — outbound notifications; secrets decrypted in memory
   only at send time.

## Data flows

- The background collector pulls current-state security data from Graph every
  ~15 minutes and persists it to SQL.
- The browser SPA reads that data through the API.
- Privileged/mutating actions (acknowledge, snooze, settings, user management)
  are authenticated and, where applicable, role-gated server-side.

## Threats & mitigations (STRIDE-aligned)

| Threat | Mitigation |
|--------|-----------|
| **Spoofing** — unauthorised access to the dashboard | Entra ID sign-in (in progress); network isolation; tenant-scoped login |
| **Tampering** — modifying data/config | Server-side authorization; read-only Graph (no tenant writes); append-only audit trail |
| **Repudiation** — denying an action | Audit entries capture actor identity from the validated token |
| **Information disclosure** — leaking secrets or data | Secrets DPAPI-encrypted at rest and never returned by the API; generic error messages; data stays in-tenant; HTTPS in production |
| **Denial of service** | Self-hosted/internal exposure limits blast radius; Graph 429 handling with backoff |
| **Elevation of privilege** | Role policies enforced on the server, not just hidden in the UI; last-admin lockout guards |

## Out of scope / assumptions

- The host OS, SQL Server, and network are administered and patched by the operator.
- The Entra app registration is correctly configured with least-privilege,
  read-only permissions.
- Vigil365 is **not** a SIEM and does not ingest raw logs; visibility is bounded
  by what Microsoft Graph exposes.
- Physical security and tenant-admin trust are assumed.

## Residual risks

- A compromised Graph credential exposes read access to tenant security data —
  hence credential storage in a vault and rotation are operator responsibilities.
- Until identity sign-in is merged to the released branch, network isolation is
  the primary access control for the public release.
