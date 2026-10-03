# Administration & Configuration

Vigil365 provides built-in tools for managing who has access to the dashboard and configuring how the system behaves. All administrative actions are recorded in a tamper-evident audit log.

## User Management & RBAC

Access to Vigil365 is controlled via in-app Role-Based Access Control (RBAC). When the application is first installed via the setup wizard, the user who runs the setup is automatically granted the **Admin** role.

From the **Administration > User Management** page, Admins can invite other users from your Microsoft 365 tenant.

### Available Roles

| Role | Permissions |
|------|-------------|
| **Admin** | Full access. Can invite/remove users, change application configuration, configure notification channels, and modify alert policies. |
| **Analyst** | Triage access. Can acknowledge, resolve, and snooze alerts, and create, edit and delete alert policies. Can view all reports, dashboards, and investigations, but cannot change system configuration or invite users. In MSP mode an Analyst manages only their clients' own policies: install-wide policies (which apply to every client), importing a policy pack, and enabling coverage that creates or switches one on are Admin-only (**403**). |
| **Viewer** | Read-only access. Can view all dashboards, alerts, and reports, but cannot modify alert states or configurations. |

### Tamper-Evident Audit Trail
To ensure accountability, every privileged action taken within Vigil365 (e.g., inviting a user, changing a role, modifying a policy) is recorded in the **Audit Log**. This log is SHA-256 hash-chained, meaning that any attempt to manually tamper with or delete records in the underlying SQL database will be detected and flagged by the application.

In MSP mode each entry names the client it concerned. MSP-level actions (users, sign-ins, client assignments, install-wide notification settings and policies, Setup credentials, unrestricted API tokens, audit export) name no client, whichever client is selected. Entries outlive a purged client (their Client column then reads "Removed client"), and the purge itself is recorded against that client. Entries written since 1.2.0 hash the client too (`HashVersion` 1); older entries keep version 0 and still verify. The CSV export ends with `TenantId` and `HashVersion` columns. Retention (`Retention:AuditEntriesDays`, default 365) prunes the one chain once a day, oldest first, across all clients.

---

## Database Engine

Vigil365 stores everything in one relational database, on either **SQL Server**
(Express, Standard, or Azure SQL) or **PostgreSQL 14+**. The engine is chosen by
`Database:Provider`; the connection string lives in
`ConnectionStrings:DefaultConnection` as before.

```json
"Database": { "Provider": "SqlServer" },
"ConnectionStrings": {
  "DefaultConnection": "Server=.\\SQLEXPRESS;Database=M365SecurityDashboard;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True"
}
```

```json
"Database": { "Provider": "Postgres" },
"ConnectionStrings": {
  "DefaultConnection": "Host=localhost;Database=vigil365;Username=vigil365;Password=..."
}
```

`Provider` defaults to `SqlServer`, so an install that predates this setting keeps
working untouched. In Docker, set `Database__Provider` and the connection string as
environment variables; `docker-compose.postgres.yml` is a ready-made Postgres stack.

The schema is created and upgraded automatically at startup on both engines. Each
engine has its own migration history, so a database can never receive the other
engine's DDL. **Switching engines on a live install is not a config change** — it is
a data migration (export, re-import), and the same is true of moving between SQL
Server editions only in the sense that a backup/restore is required.

**Which engine?** For a single-tenant install, SQL Server Express is ample — the
database stays well under 1 GB. Choose Postgres or a licensed SQL Server edition
when you expect to exceed Express's 10 GB / 1.4 GB-memory limits, which in
practice means the MSP multi-tenant edition (see `docs/MSP_EDITION_PLAN.md`).
The Metrics tab shows the live database size on either engine.

## Tenants

Vigil365 keeps every alert, run, snapshot, note and audit event tagged with the
tenant it belongs to. A single-organisation install has one tenant — created for
you on upgrade as "Default" — and never needs to think about this.

**Edition mode.** `Edition:Mode` is `Single` (default) or `Msp`; the installer sets
it. Single mode hides everything below: no Clients page, no switcher, and the API
refuses a second client. In MSP mode the header switcher scopes the dashboard to
one client at a time (the API reads it from the `X-Vigil-Tenant: <tenant id>`
header). With several clients and none chosen, the app opens on **Clients** and
tenant-data requests return **400** rather than mixing clients' data. In both modes
sign-in is accepted only from the install's own Entra tenant.

### Onboarding a client tenant (MSP)

1. **Clients → Add client**: name it (the Entra tenant id is optional — it is
   recorded on consent).
2. **Sign in as global admin & consent**: a Microsoft popup opens. The client's
   Global Administrator signs in and approves the read-only permissions. The
   popup lands on Vigil365's `/consented` page, which records the consent.
3. The dialog tests the connection on its own, records the client's Entra id and
   shows **Connected**. Collection starts on the next cycle.

If it does not connect, the dialog says why inline: popup blocked (allow popups,
or use the copy-link option), consent declined (Microsoft's error is shown), the
window was closed first, or a 5-minute timeout. Nothing is half-saved — retry
when ready. If the client's admin cannot sign in from your screen, open **Can't
sign in here? Send the client a link instead**, copy the consent link to them, and
press **Test now** once they confirm. Until their admin consents the client is not
connected (Test now reports "No Graph credentials apply to this tenant") and
nothing is collected for it.

A consent link expires after 30 minutes and works once: after it has recorded
consent it is dead, so re-consent (for example after a new permission) needs a new
link from the dialog. A declined or refused consent does not use it up. One
Microsoft tenant can belong to only one client: consent, **Test now** or an edit
that would give a client an Entra id another client (active or not) already has is
refused.

The **MSP app readiness** card on Clients checks that the app registration is
multi-tenant, has the `/consented` redirect and every permission in
`graph-permissions.json`. It needs `Application.Read.All` in **your** tenant
(granted by the installer, or by `register-app.ps1 -MultiTenant`); without it the
card shows "not checked".

**Day to day.** The Clients page shows every client worst-first and an **Open
alerts across clients** queue: acknowledge or resolve in place (the toast names
the client), or click an alert to switch to its client. The audit log (User
Management) shows one list across clients, with a Client column, for MSP Admins.

#### Appendix: the same steps through the API

All endpoints are Admin-only and audited.

1. `POST /api/tenants` `{ "name": "Contoso", "microsoftTenantId": "<entra tenant guid>" }`
2. Optional: by default the client uses the install's shared MSP app once its admin
   has consented. To give it an app of its own instead,
   `PUT /api/tenants/{id}/credentials` `{ "clientId": "...", "clientSecret": "..." }`.
   The secret is encrypted at rest and never returned.
3. `GET /api/tenants/{id}/consent-url?redirectUri=https://your-host/consented` — send
   the URL to the client's Global Administrator. They sign in and grant admin consent.
4. `POST /api/tenants/{id}/test` — Vigil365 calls Graph as that tenant, confirms the
   Entra tenant id matches, and records the consent time. Collection starts on the
   next cycle; `GET /api/tenants` shows each client's last collection status and error.

Non-Admin staff see only the clients assigned to them (**User Management →
Clients** column); Admins see every client. Non-Admins see routing and per-client
policy overrides read-only.

**Where a client's alerts go.** By default every client's alerts go to the MSP's own
channels and default recipient (Settings → Notifications). Per client, an Admin can
also — or instead — send them to the client's own address, Teams or webhook, and
set a per-client minimum severity: `PUT /api/notification-routing` with the client
selected (`X-Vigil-Tenant`). Install-wide alert policies apply to every client; an
Admin can switch one off or change its threshold for a single client
(`PUT /api/alert-policies/{id}/tenant-override`), or create a client-only policy by
ticking **This client only** when drafting it. Enable the daily **MSP digest** in
notification settings (`mspDigestEnabled`, `mspDigestHourUtc`) to receive one email
summarising every client, worst first.

A policy's **Notify Email**, or a client's override address for it, receives that
policy's alerts in place of the MSP's default recipient. With **MSP and client**
routing the client's own recipient still gets a copy. With client-only routing
nothing goes to the MSP side, including that address, and saving client-only
routing with no client email, Teams or webhook is refused (**400**). A client's own
webhook is never signed with the MSP's webhook signing secret.

**Report schedules** belong to the client selected when they were created; in MSP
mode creating one with no client selected is refused (**400**). Schedules made
before the install was converted to MSP belong to no client and are no longer sent:
they show "skipped: not assigned to a client" — delete each and create it again
with its client selected.

**Certificates, backoff and branding.** A client's credentials may be a certificate
(thumbprint in the server's store, or a PFX path and password) instead of a secret;
the certificate wins when both are set. A client whose collection keeps failing is
retried with exponential backoff (interval × 2ⁿ, capped by `Graph:MaxBackoffMinutes`);
storing new credentials, re-activating the client, or one successful run resets it.
`Graph:TenantParallelism` and `Graph:TenantStaggerSeconds` control how many clients
collect at once. A **brand name** and accent colour on the client replace "Vigil365"
on that client's digest emails, CSVs and PDFs. `/health` reports the database size
and flags `sizeWarning` above `Database:SizeWarningBytes` (default 8 GiB). For DPA
reviews, see `docs/MSP_DATA_PROCESSING.md`.

**SIEM tokens.** **User Management → API tokens → New token.** The token is shown
once. In MSP mode, **Restrict to client** limits it to one client; an unrestricted
token in a multi-tenant install must send `X-Vigil-Tenant` on every request.

**Database size.** Admins see a banner when `/health` reports `sizeWarning`
(above `Database:SizeWarningBytes`, default 8 GiB) — on SQL Server Express that is
the cue to shorten retention or move engines before the 10 GB write limit.

**Which credentials a client uses.** Its own credentials, if set, always win.
Otherwise, in MSP mode: a client whose admin has consented (its Entra id is
recorded) uses the shared MSP app — the install's client id and secret or
certificate — in its own tenant; a client with no Entra id has not consented, is
reported as not connected and is never collected; only the **Default** client, or
one whose Entra id is the install's own, uses the install's credentials as they
are. In Single mode the one tenant uses the install's credentials. `DELETE
/api/tenants/{id}` deactivates (data kept); `?purge=true` deletes the tenant and
every row it owned, except its audit log entries (see the audit trail above).

## Initial Setup & Configuration

When you launch Vigil365 for the first time, you will be guided through a setup checklist to ensure the dashboard can successfully collect data from your tenant.

### 1. Microsoft Graph Connection
Vigil365 requires a connection to your Microsoft 365 tenant to aggregate security alerts. 
If you used the Interactive Setup Wizard, this Entra ID App Registration was created automatically. 
If the dashboard reports missing permissions, navigate to **Administration > Setup** to view exactly which Graph API permissions are missing and grant Admin Consent in the Azure Portal.

### 2. Notification Channels (SMTP & Webhooks)
To receive alerts outside of the dashboard, you must configure your notification channels:
- **Email (SMTP):** Configure your SMTP server details to enable Daily/Weekly Executive Digest reports and email alerts.
- **Teams / Slack / Generic Webhooks:** You can route specific alert policies to external chat channels or SIEMs.

All secrets (like SMTP passwords and webhook URLs) are encrypted at rest with ASP.NET Core Data Protection (key ring in `DataProtection:KeyPath`). The SMTP password and webhook signing secret are never returned by the API. Webhook URLs are returned only to Admins, so they can edit them; other roles see only whether one is set.

### 3. Alert Policies
By default, Vigil365 imports a set of best-practice alert policies. You can customize these thresholds or create entirely new anomaly/activity-based policies in the **Alert Center > Policies** tab. 

An open alert whose policy is disabled, switched off for the client, or deleted resolves on its own shortly afterwards. **Suppression rules** that name entities (users, devices, an activity's target) suppress an alert only when every entity it affects is covered; a rule for one service account no longer hides the other users in the same alert.

**Collection.** A run is **Failed** only when every source fails, including the directory audit pull; otherwise it completes and lists the sources that failed. Collected alerts that drop out of a Graph feed are resolved after a complete read, so the first run after upgrading to 1.2 may resolve many long-stale items (old failed sign-ins, closed advisories, remediated risky users) and lower the counts.

*Tip: Before enabling a new policy, use the "Dry Run" feature to backtest it against your historical data to see how many times it would have fired in the past 30 days.*
