# M365 Security Alert Dashboard

A self-hosted Microsoft 365 security monitoring dashboard that aggregates alerts from Defender XDR, Entra ID Protection, Intune, Exchange Online, Compliance, and more — all in one place, collected on a schedule (every 15 minutes by default).

> **No third-party SaaS required.** Runs entirely on your own Windows host using Microsoft Graph API.

---<img width="1360" height="679" alt="dashboard_redacted" src="https://github.com/user-attachments/assets/f1d6675e-34fb-4f60-b48b-95995147e772" />


## Features

### Pages & Monitoring Coverage

| Page | What it monitors |
|------|-----------------|
| **Overview** | Secure Score trend, KPI tiles, recent alerts, risky users, compliance summary, alert policy status |
| **Identity** | Risky users, risky sign-ins, risk detections, MFA coverage, PIM assignments, MDI alerts, foreign sign-ins |
| **Devices** | Intune compliance, non-compliant devices, stale devices, MDE endpoint alerts |
| **Email** | MDO protection alerts by category (malware, phish, spam), threat breakdown |
| **Incidents & Alerts** | Unified Defender XDR incidents + alerts, severity KPI tiles, date-range filter |
| **Compliance** | DLP alerts, MCAS alerts, IRM insider risk alerts, attack simulations |
| **Alert Center** | Custom alert policies, policy templates, triggered alert history, acknowledge/resolve workflow |
| **Service Health** | M365 service advisories and incidents, per-service health status |
| **M365 Connectivity** | Sign-in health, connectivity issues |
| **Licenses & Users** | License SKU breakdown, inactive users, expiring licenses |
| **Conditional Access** | Policy list, state breakdown (Enabled/Report-only/Disabled), per-policy detail, gap analysis |
| **Tenant Activity** | Directory audit events with search, day-range filter, and CSV export — the audit surface behind activity-based alerting |
| **Sign-in Locations** | Success/failure breakdown and country drill-down (tabular; no map) |

### Enterprise Features

- **Alert Policy Engine** — metric, activity, and anomaly policies (MFA drop, risky-user spike, role assignment, app-consent, PIM changes, and more) with thresholds; auto-evaluates against live data. Triggered alerts are stored server-side in SQL Server with a full acknowledge / snooze / resolve / assign / notes workflow.
- **Activity & Anomaly Alerting** — alerts on tenant *audit activity* (privileged role changes, app credential adds, CA policy edits) and on statistical spikes, not just static thresholds.
- **Notifications** — Microsoft Teams, email (SMTP), and generic webhook delivery, with per-channel digest mode and delivery-failure self-alerting.
- **Reports** — scheduled executive digest (daily/weekly/monthly) over email with a CSV attachment, plus a live preview.
- **Trends** — historical posture tracking (Secure Score, risky users, compliance) from periodic snapshots.
- **Recommendations** — a single findings hub folding in Conditional Access gaps and SharePoint/OneDrive sharing posture.
- **Entity Investigation** — drill into any user or device for a merged timeline of its alerts and audit activity.
- **RBAC & User Management** — in-app Admin / Analyst / Viewer roles, invitations, and a tamper-evident (SHA-256 hash-chained) audit trail of privileged actions.
- **Global Search** — Ctrl+K palette across alerts, users, devices, and pages.
- **Detail Panels** — click any alert, user, device, or policy for all fields and a direct "View in M365 Portal →" deep link.
- **Search, Filter, Export** — full-text search, dropdown filters, and CSV export on every page (sortable columns on the active-alert queue).
- **Dark Mode**, **collapsible sidebar**, **toast notifications**, **saved filter presets**, and a **responsive layout**.

---

## Stack

| Layer | Technology |
|-------|-----------|
| Backend | ASP.NET Core 8 Minimal API |
| Frontend | React 18 + TypeScript + Vite |
| Sign-in | Microsoft Entra sign-in (MSAL) + in-app RBAC |
| Collection | Graph app-only — client secret **or** certificate |
| Scheduler | .NET BackgroundService — every 15 minutes |
| Storage | SQL Server Express (EF Core migrations) |
| Icons | lucide-react |

---

## Prerequisites

**Docker install (Option 1)** — just [Docker](https://docs.docker.com/get-docker/)
(Windows, Linux, or macOS). Everything else runs in containers.

**Windows / manual install (Options 2–3):**
1. Windows 10/11 or Windows Server 2019+
2. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or ASP.NET Core 8 Hosting Bundle)
3. [SQL Server Express](https://www.microsoft.com/en-us/sql-server/sql-server-downloads) (free)
4. [Node.js 20+](https://nodejs.org/)
5. [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) — only if you use `register-app.ps1`

All paths also need a Microsoft 365 tenant where you can create an app registration.

---

## Microsoft Entra App Registration

> **Tip:** `register-app.ps1` does all of this for you (permissions, redirect URI,
> exposed scope, secret, admin consent). Do it manually only if you prefer.

### Create the app

1. Go to [Entra admin center](https://entra.microsoft.com) → **App registrations** → **New registration**
2. Name it (e.g. `Vigil365`)
3. Select **Accounts in this organizational directory only**
4. Under **Redirect URI**, choose **Single-page application (SPA)** and enter the URL you'll serve the app on (e.g. `http://localhost:8080` for Docker, or `https://vigil365.yourco.local:5001`)
5. Click **Register**, then note the **Tenant ID** and **Application (client) ID**
6. **Certificates & secrets** → **New client secret** — copy the value immediately
7. **Expose an API** → set Application ID URI to `api://<client-id>` → **Add a scope** named `access_as_user` (this is what the browser sign-in requests)

### Required API permissions (Application, not Delegated)

Grant **admin consent** for all of these:

| Permission | Used for |
|-----------|---------|
| `SecurityAlert.Read.All` | Defender XDR alerts |
| `SecurityIncident.Read.All` | Defender XDR incidents |
| `IdentityRiskyUser.Read.All` | Entra ID risky users |
| `IdentityRiskEvent.Read.All` | Risk detections |
| `AuditLog.Read.All` | Sign-in logs, audit logs |
| `Reports.Read.All` | MFA registration, auth methods |
| `DeviceManagementManagedDevices.Read.All` | Intune devices |
| `ServiceHealth.Read.All` | M365 service health |
| `Policy.Read.All` | Conditional Access policies |
| `Directory.Read.All` | Users, groups, PIM |
| `PrivilegedAccess.Read.AzureAD` | PIM assignments |
| `ThreatHunting.Read.All` | Advanced hunting / MDI |
| `UserAuthenticationMethod.Read.All` | MFA method details |
| `SharePointTenantSettings.Read.All` | SharePoint/OneDrive sharing posture |
| `AttackSimulation.ReadWrite.All` | Attack-simulation results (read-only in-app; Graph has no read-only variant — optional) |

The in-app **Graph Permissions** reference (below Collection Runs) shows each of
these with a live granted/missing status inferred from the last collection run.

> Some features (IRM, Attack Simulation, Identity Health) require additional Purview/Defender licensing in your tenant. The dashboard gracefully shows a permission error card for unavailable features.

---

## Install

You need **one** thing first: an **Entra app registration** (so Vigil365 can read
your tenant via Graph). You can let the new **Interactive Setup Wizard** create this automatically, or create it manually
(see [Microsoft Entra App Registration](#microsoft-entra-app-registration)).

### Install (Interactive Setup Wizard)

We provide a robust native C# Interactive Windows GUI Installer that will:
1. Verify and automatically download prerequisites (.NET 8, Node.js, Azure CLI, SQL Server Express).
2. Authenticate with Azure to create the App Registration.
3. Build the frontend and publish the backend.
4. Set up HTTPS, including the certificate.
5. Deploy the application as an auto-starting Windows Service, and open the firewall.

To launch the native Windows setup wizard, simply run this command from the project root:

```bash
dotnet run --project src/M365SecurityDashboard.GuiInstaller
```

Run it **as Administrator** — it registers a Windows service and creates a SQL
login for it.

#### Choose who needs to reach it

The wizard's first question decides everything else:

**Just this computer** — the default, for evaluating Vigil365. It binds
`http://localhost:8080` (loopback only), needs no certificate, and changes no
firewall rules. Entra permits `http` for loopback redirect URIs, so sign-in works
as-is. Nothing else on your network can reach it.

**Other people on our network** — asks for a hostname and a certificate. Entra
refuses plain `http://` redirect URIs for anything but localhost, so once
Vigil365 is reachable by name, HTTPS is required and the wizard asks where the
certificate comes from:

| Option | Use when |
| --- | --- |
| **A certificate already on this server** | Your organisation issues certificates from an internal CA (most common). The wizard lists what is in `LocalMachine\My` and marks hostname matches with a ✓. |
| **A `.pfx` file** | You hold a wildcard or externally-issued certificate as a file. |
| **Create one for me** (default) | You have neither and want to get running now. |

The last option generates a certificate and trusts it **on that server only** —
that machine stops warning, every other browser still warns. Fine for a pilot,
not fine to leave in place: on a security product, a warning you tell people to
click through is training them to ignore the one that matters. Re-run the wizard
and pick one of the first two options to replace it; re-running reuses the
existing Entra app registration rather than creating another.

If the server is reachable from the internet, `scripts/request-cert.ps1` gets a
free, publicly-trusted certificate from Let's Encrypt instead:

```bash
pwsh -File scripts/request-cert.ps1 -Hostname vigil365.yourcompany.com -Email you@yourcompany.com
```

Everything else is detected or derived: the first administrator is taken from
your Azure CLI sign-in, and an existing SQL Server instance is found and reused
rather than installing a second one.

After the wizard finishes, open the app and finish **Setup** in the browser to
supply the Graph credentials used for collection.



## HTTPS / TLS (required for production)

Outside Development the app enforces HTTPS (HSTS + redirect). Plain HTTP is only
for local development. Two supported ways to serve TLS:

### Option A — Reverse proxy (recommended)

Terminate TLS at IIS / Nginx / Caddy and proxy to the app on localhost. Example
Caddy config:

```
vigil365.yourcompany.com {
    reverse_proxy localhost:8080
}
```

Run the app bound to localhost only (`--urls http://localhost:8080`) so it is
never directly exposed; the proxy handles certs (e.g. automatic Let's Encrypt).

### Option B — Kestrel with a certificate

Let the app terminate TLS directly by configuring a Kestrel HTTPS endpoint in
`appsettings.Production.json` (Kestrel reads this automatically — no code change):

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:443",
        "Certificate": { "Path": "C:\\certs\\vigil365.pfx", "Password": "YOUR_PFX_PASSWORD" }
      }
    }
  }
}
```

Set the Azure App Registration **SPA redirect URI** and `Auth:RedirectUri` to the
HTTPS URL (e.g. `https://vigil365.yourcompany.com`).

> **Credential hygiene:** prefer **certificate auth** for Graph over a client
> secret, store secrets in a vault or environment variables (never in committed
> files), and rotate any secret that has ever been exposed.

---

## API Reference

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/dashboard/overview` | Aggregated overview data |
| `GET` | `/api/dashboard/identity` | Identity & MFA data |
| `GET` | `/api/dashboard/devices` | Intune device compliance |
| `GET` | `/api/dashboard/email-protection` | MDO email alerts |
| `GET` | `/api/dashboard/security-incidents` | Defender XDR incidents |
| `GET` | `/api/dashboard/defender-alerts` | Defender XDR alerts |
| `GET` | `/api/dashboard/mdi-alerts` | Microsoft Defender for Identity alerts |
| `GET` | `/api/dashboard/mcas-alerts` | Defender for Cloud Apps alerts |
| `GET` | `/api/dashboard/risk-detections` | Entra ID risk detections |
| `GET` | `/api/dashboard/attack-simulation` | Attack simulation results |
| `GET` | `/api/dashboard/servicehealth` | M365 service health |
| `GET` | `/api/dashboard/licenses` | License SKU usage |
| `GET` | `/api/dashboard/conditional-access` | CA policies |
| `GET` | `/api/dashboard/ca-gaps` | Conditional Access gap analysis |
| `GET` | `/api/dashboard/sharing-posture` | SharePoint/OneDrive sharing posture |
| `GET` | `/api/dashboard/signin-locations` | Sign-in locations |
| `GET` | `/api/audit-events` | Tenant directory audit events |
| `GET` | `/api/entity/{kind}/{id}` | Entity investigation timeline (user/device) |
| `GET` | `/api/setup/status` | First-run setup progress |
| `GET` | `/api/setup/permissions` | Graph permission reference + status |
| `POST` | `/api/collector/run` | Trigger manual data collection |
| `GET` | `/api/collector/runs` | Collection run history |

> Unknown `/api/*` paths return `404` JSON. The full surface (alerts workbench,
> notification settings, report schedules, RBAC) is larger than this excerpt.

---

## Security & Maturity

> **Read this before relying on Vigil365.** This is an open-source **read-only visibility aggregator**, currently **beta**. It surfaces signals that already exist across your Microsoft 365 admin centers in one place. It is **not** a replacement for native Microsoft security tooling (Defender XDR, Entra ID Protection, Purview), and it does **not** make security decisions or change configuration for you. Treat its output as a convenience view, verify findings in the source portal before acting, and do your own review of the code before deploying it in a sensitive environment.

### What is in scope by design

- **Read-only, least privilege.** Nearly every Graph permission requested is `*.Read.All`. The one exception is `AttackSimulation.ReadWrite.All`, which Microsoft Graph offers with no read-only variant — the app only reads with it and never launches simulations. If you don't use the attack-simulation view, don't grant it. The app **cannot modify** users, devices, policies, or tenant settings.
- **Recommends, never remediates.** The Recommendations view and "Fix in M365 Portal →" links tell you what to change and deep-link you to the right blade, but the app makes **no** changes itself — every remediation happens in Microsoft's tooling, by you.
- **No inbound exposure by default.** In development the API binds to `localhost`. A production deployment (`deploy.ps1`) runs behind Kestrel with a TLS certificate; anything beyond localhost is a deliberate choice you make.
- **App-only collection** via MSAL (`Azure.Identity`) using a client secret or certificate; **user sign-in** via Entra with in-app RBAC. Standard Microsoft auth, not a homegrown scheme. All Graph traffic is HTTPS/TLS.

### How credentials and secrets are handled

- The Graph client secret is **never** committed to source. Use .NET User Secrets (dev) or `appsettings.Production.json` / environment variables (prod, both gitignored).
- Notification secrets stored in the database (SMTP password, Teams/Slack & generic webhook URLs) are **encrypted at rest with the Windows Data Protection API (DPAPI), machine scope** — a leaked database row cannot be decrypted on another machine. Secrets are decrypted only in memory at send time and the SMTP password is never returned by the API.
- **Recommended:** use **certificate-based authentication** instead of a client secret for production. A non-exportable certificate in the Windows cert store removes the plaintext shared secret entirely; Vigil365 supports a certificate thumbprint or PFX path, with a secret only as a fallback.

### Host hardening checklist (your responsibility)

The security of this app is only as good as the box it runs on. Before production use:

- [ ] Run on a **dedicated, patched, hardened** Windows host — not a shared workstation or a machine that handles untrusted input
- [ ] Run the service under a **dedicated low-privilege service account**, not an admin or your own login
- [ ] Enable **BitLocker / full-disk encryption** so the database and secrets are protected at rest
- [ ] Keep the host **off the public internet**; access the dashboard over the LAN/VPN only
- [ ] If you must expose it, put it behind a **reverse proxy with TLS and authentication**
- [ ] Ensure the host has **endpoint protection** and is **monitored** — a compromised host can read tokens in memory while the app runs
- [ ] **Rotate the Graph secret/certificate** on a schedule and immediately if the host is ever suspected compromised
- [ ] Restrict who can read `appsettings.Production.json` and the SQL database with NTFS/SQL permissions

### Operational resilience

- Rate limiting is handled automatically (429 `Retry-After` respected).
- A failed individual Graph source does not stop the whole collection run; each card degrades independently.
- Logs are newline-delimited JSON on stdout and in `logs/vigil365-.json` beside the app. Files roll daily (and at 10 MB) with the newest 14 files retained. Configure `Logging__File__Path`, `Logging__File__RetainedFileCountLimit`, and `Logging__File__FileSizeLimitBytes` for the host policy. Docker persists them in the `vigil365-logs` volume at `/app/logs`.
- Log events include request correlation IDs and structured fields. Do not put access tokens, client secrets, or notification credentials in log messages.
- Follow the [Operations Runbook](docs/OPERATIONS_RUNBOOK.md) for SQL/key-ring backups, restore drills, and upgrades.

> Found a security issue? See [SECURITY.md](SECURITY.md) — please report privately, not in a public issue.

---

## Contributing

Pull requests welcome. Please:
- Do not commit credentials or tenant-specific data
- Keep `appsettings.json` with placeholder values only
- Test with `npm run build` and `dotnet build` before submitting

---

## License

MIT — see [LICENSE](LICENSE)
