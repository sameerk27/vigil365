# M365 Security Alert Dashboard

A self-hosted, real-time Microsoft 365 security monitoring dashboard that aggregates alerts from Defender XDR, Entra ID Protection, Intune, Exchange Online, Compliance, and more — all in one place.

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
| **Conditional Access** | Policy list, state breakdown (Enabled/Report-only/Disabled), per-policy detail |
| **Audit Log** | Unified audit log with category filter, actor/target detail |
| **Sign-in Locations** | Geographic sign-in map, success/failure breakdown, country drill-down |

### Enterprise Features

- **Alert Policy Engine** — define custom policies (MFA drop, risky user spike, device breach) with thresholds; auto-evaluates against live data and tracks triggered alerts in browser localStorage
- **9 Pre-built Alert Templates** — one-click templates for common security scenarios
- **Detail Modals** — click any alert, user, device, or policy to see all available fields and a direct "View in M365 Portal →" deep link
- **Search, Filter, Sort, Export** — every page has full-text search, dropdown filters, sortable columns, and CSV export
- **Saved Filter Presets** — save and reload custom filter combinations per page (localStorage)
- **Dark Mode** — full dark/light theme toggle, persisted across sessions
- **Collapsible Sidebar** — icon-only collapsed mode with hover tooltips
- **Toast Notifications** — on export, preset save, policy actions
- **Sticky Filter Bars** — filter controls stay visible while scrolling long lists
- **Responsive Layout** — collapses to single-column below 900px

---

## Stack

| Layer | Technology |
|-------|-----------|
| Backend | ASP.NET Core 8 Minimal API |
| Frontend | React 18 + TypeScript + Vite |
| Auth | Microsoft Graph — Client Credentials (app-only) |
| Scheduler | .NET BackgroundService — every 15 minutes |
| Storage | SQL Server Express (alerts + collection runs) |
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

> Some features (IRM, Attack Simulation, Identity Health) require additional Purview/Defender licensing in your tenant. The dashboard gracefully shows a permission error card for unavailable features.

---

## Install

You need **one** thing first: an **Entra app registration** (so Vigil365 can read
your tenant via Graph). Script it with `register-app.ps1`, or create it manually
(see [Microsoft Entra App Registration](#microsoft-entra-app-registration)).

```powershell
az login
.\register-app.ps1 -RedirectUri http://localhost:8080
```
This prints your **Tenant ID**, **Client ID**, and **Client Secret** — keep them for the steps below.

Then pick the install that fits you. **You never edit a config file for Graph
credentials** — you enter them in the in-app Setup wizard after first sign-in.

### Install (enterprise)

Before installing, have a SQL Server database, a public HTTPS URL, and an Entra
app registration ready. Then run one command. The installer asks for the rest.

```powershell
# Windows — run PowerShell as Administrator
.\enterprise-install.ps1
```

```bash
# Linux
sudo ./enterprise-install.sh
```

After it finishes: point your HTTPS proxy to `http://127.0.0.1:8080`, add the
same public URL to Entra as a SPA redirect URI, then open the app and finish
**Setup**.

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
| `GET` | `/api/dashboard/email` | MDO email alerts |
| `GET` | `/api/dashboard/compliance` | DLP/MCAS/IRM alerts |
| `GET` | `/api/dashboard/incidents` | Defender XDR incidents |
| `GET` | `/api/dashboard/mdi-alerts` | Microsoft Defender for Identity alerts |
| `GET` | `/api/dashboard/mcas-alerts` | Defender for Cloud Apps alerts |
| `GET` | `/api/dashboard/insider-risk` | Insider Risk Management alerts |
| `GET` | `/api/dashboard/risk-detections` | Entra ID risk detections |
| `GET` | `/api/dashboard/identity-health` | Identity health issues |
| `GET` | `/api/dashboard/attack-simulation` | Attack simulation results |
| `GET` | `/api/dashboard/service-health` | M365 service health |
| `GET` | `/api/dashboard/licenses` | License SKU usage |
| `GET` | `/api/dashboard/conditional-access` | CA policies |
| `GET` | `/api/dashboard/audit-log` | Unified audit log |
| `GET` | `/api/dashboard/sign-ins` | Sign-in locations |
| `POST` | `/api/collector/run` | Trigger manual data collection |
| `GET` | `/api/collector/runs` | Collection run history |

---

## Security & Maturity

> **Read this before relying on Vigil365.** This is an open-source **read-only visibility aggregator**, currently **beta**. It surfaces signals that already exist across your Microsoft 365 admin centers in one place. It is **not** a replacement for native Microsoft security tooling (Defender XDR, Entra ID Protection, Purview), and it does **not** make security decisions or change configuration for you. Treat its output as a convenience view, verify findings in the source portal before acting, and do your own review of the code before deploying it in a sensitive environment.

### What is in scope by design

- **Read-only, least privilege.** Every Graph permission requested is `*.Read.All`. The app **cannot modify** users, devices, policies, or tenant settings even if the host is compromised.
- **No remediation automation.** "View in M365 Portal →" links only deep-link you to the correct blade. The app never tells you what to change and never makes changes — remediation stays in Microsoft's tooling where it belongs.
- **No inbound exposure by default.** The API binds to `localhost`. Remote access requires you to deliberately open a firewall port (and you should front it with TLS + auth if you do).
- **App-only client-credentials flow** via MSAL (`Azure.Identity`). Standard Microsoft auth, not a homegrown scheme. All Graph traffic is HTTPS/TLS.

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
