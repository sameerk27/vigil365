# M365 Security Alert Dashboard

A self-hosted, real-time Microsoft 365 security monitoring dashboard that aggregates alerts from Defender XDR, Entra ID Protection, Intune, Exchange Online, Compliance, and more — all in one place.

> **No third-party SaaS required.** Runs entirely on your own Windows host using Microsoft Graph API.

---

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

1. Windows host (Windows 10/11 or Windows Server 2019+)
2. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or ASP.NET Core 8 Hosting Bundle
3. [SQL Server Express](https://www.microsoft.com/en-us/sql-server/sql-server-downloads) (free)
4. [Node.js 20+](https://nodejs.org/)

---

## Microsoft Entra App Registration

### Create the app

1. Go to [Entra admin center](https://entra.microsoft.com) → **App registrations** → **New registration**
2. Name it (e.g. `M365SecurityDashboard`)
3. Select **Accounts in this organizational directory only**
4. No redirect URI needed
5. Click **Register**
6. Note the **Tenant ID** and **Application (client) ID**
7. Go to **Certificates & secrets** → **New client secret** — note the secret value immediately

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

## Setup

### 1. Clone and configure secrets

```powershell
git clone https://github.com/YOUR_ORG/m365-security-dashboard.git
cd m365-security-dashboard

cd src\M365SecurityDashboard.Api
dotnet user-secrets init
dotnet user-secrets set "Graph:TenantId"     "YOUR_TENANT_ID"
dotnet user-secrets set "Graph:ClientId"     "YOUR_CLIENT_ID"
dotnet user-secrets set "Graph:ClientSecret" "YOUR_CLIENT_SECRET"
```

> **Never put real credentials in `appsettings.json`** — use User Secrets for development and environment variables or `appsettings.Production.json` (gitignored) for production.

### 2. Set up the database

```powershell
# Option A: let the API auto-create on first run (requires db-create rights)
# Option B: pre-create manually
sqlcmd -S .\SQLEXPRESS -E -i .\database\schema.sql
```

### 3. Build the frontend

```powershell
cd src\m365-security-dashboard-client
npm install
npm run build
Copy-Item -Recurse -Force .\dist\* ..\M365SecurityDashboard.Api\wwwroot\
```

### 4. Run the API

```powershell
cd src\M365SecurityDashboard.Api
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
```

Open **http://localhost:5000**

---

## Development (hot-reload)

Run both simultaneously:

```powershell
# Terminal 1 — backend
cd src\M365SecurityDashboard.Api
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet watch run

# Terminal 2 — frontend
cd src\m365-security-dashboard-client
npm run dev
```

Frontend dev server: `http://localhost:5173` (proxies API calls to backend)

---

## Production Deployment (Windows Service)

```powershell
# 1. Build frontend
cd src\m365-security-dashboard-client
npm install && npm run build
Copy-Item -Recurse -Force .\dist\* ..\M365SecurityDashboard.Api\wwwroot\

# 2. Publish API
cd ..\M365SecurityDashboard.Api
dotnet publish -c Release -o C:\Apps\M365SecurityDashboard

# 3. Create appsettings.Production.json in publish folder
# (see template below — this file is gitignored)

# 4. Install as Windows Service
sc.exe create M365SecurityDashboard `
  binPath= "C:\Apps\M365SecurityDashboard\M365SecurityDashboard.Api.exe --environment Production --urls http://localhost:8080" `
  start= auto
sc.exe start M365SecurityDashboard
```

**`appsettings.Production.json` template** (create this file manually, never commit it):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=.\\SQLEXPRESS;Database=M365SecurityDashboard;Trusted_Connection=True;Encrypt=False"
  },
  "Graph": {
    "TenantId": "YOUR_TENANT_ID",
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "CollectionIntervalMinutes": 15,
    "DevicesNotCheckedInDays": 7,
    "SignInLookbackHours": 24
  }
}
```

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

## Security Notes

- Credentials are never stored in source code — use .NET User Secrets (dev) or `appsettings.Production.json` (prod, gitignored)
- The app uses **application permissions** (app-only) — no user sign-in required
- All Graph calls use short-lived bearer tokens via `ClientSecretCredential` (MSAL)
- Rate limiting is handled automatically (429 retry-after respected)
- Failed individual Graph sources do not stop the entire collection run

---

## Contributing

Pull requests welcome. Please:
- Do not commit credentials or tenant-specific data
- Keep `appsettings.json` with placeholder values only
- Test with `npm run build` and `dotnet build` before submitting

---

## License

MIT — see [LICENSE](LICENSE)
