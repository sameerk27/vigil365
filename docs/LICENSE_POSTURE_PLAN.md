# Licence-aware posture and adoption: technical design

Status: **design, nothing built** (2026-10-19). Source brief: `license-posture-handoff-2026-10-19.md` (Samir).
Items marked **[verify]** are not yet confirmed against Microsoft or the licensor; confirm each
before the code that depends on it ships.

## 1. Decisions (Samir, 2026-10-19)

| Question | Decision |
| --- | --- |
| Standalone or Vigil365? | **A Vigil365 module**: .NET 8 API and React UI, inside the existing app. |
| v0.1 data sources | **Microsoft Graph, plus Exchange Online and Security & Compliance PowerShell** |
| Baselines | **CIS, SCuBA and Secure Score** (but see §6 on the CIS licence) |
| Licence | **Open source**, under Vigil365's MIT licence |

Multi-tenant comes from Vigil365 itself. In the MSP edition the module runs per client, like
every other collector. GDAP and Lighthouse-style delegation are out of scope.

## 2. What it produces

For each tenant, one finding per check, each with exactly one status:

| Status | Meaning |
| --- | --- |
| `Pass` | Licensed, configured to the minimum viable config. |
| `Fail` | Licensed, configured, below the minimum. |
| `LicensedNotConfigured` | The tenant owns the feature and hasn't set it up. This is the "paid for, unused" output. |
| `LicensedLowAdoption` | Configured, but usage is below a threshold. Needs usage data; phase 3. |
| `NotLicensed` | No owned SKU includes the feature. Carries the SKUs that would unlock it, for pre-sales. |
| `Unknown` | Couldn't be read, with the reason: a missing permission, PowerShell not set up, a beta endpoint that failed, or a workload not reachable. |

Every finding carries:
- its CIS, SCuBA and Secure Score references
- the entitlement it needs, the evidence read, and the data source
- **data confidence** (`ga`, or `beta` when a beta Graph endpoint was used)

Reports:
- a new **Posture** page
- a client-presentable single-file HTML export
- CSV, one row per finding
- JSON, for automation and diffs

## 3. Architecture in Vigil365

```
Collectors (per tenant, read-only)
  Graph:      subscribedSkus (service plans), CA, security defaults, auth methods, Intune, Secure Score control profiles
  PowerShell: Exchange Online + Security & Compliance (Get-* only), via a pwsh 7 child process
        │
        ▼
PostureSnapshot (one JSON per run, versioned, stored per tenant: drift and history later)
        │
        ▼
PostureRulesEngine  ◄── data/entitlements.json   (feature → required service plans → SKUs that include them)
        │           ◄── data/checks.json         (feature → checks → minimum viable config)
        │           ◄── data/baseline-map.json   (finding ↔ CIS id ↔ SCuBA id ↔ Secure Score control)
        ▼
Findings → Posture page, HTML / CSV / JSON export
```

- **Data maps are data, not code.**
  - Location: `src/M365SecurityDashboard.Api/Data/Posture/*.json`, embedded like `graph-permissions.json`.
  - Each map has a JSON schema, and a test fails on a row without a source URL and a retrieved date.
- **Entitlements are keyed by service plan, not by SKU.**
  - `subscribedSkus` already returns every owned SKU's service plans and their provisioning status, so
    "does the tenant own the feature" is answered from live data.
  - SKU names are only needed for the `NotLicensed` "what unlocks it" answer. Those come from
    Microsoft's licensing CSV (§5), for Business Premium, E3 and E5 only in v0.1.
  - No SKU or service-plan GUIDs are typed from memory.
- **Fits the existing app:**
  - per-tenant collection through `TenantIterator` and the collection worker
  - findings scoped by `ITenantScoped`
  - the existing `RecommendationsEngine` hub links to the new page; it isn't replaced

## 4. Collectors

### Graph (existing permissions unless marked)

| Check | Endpoint | Permission |
| --- | --- | --- |
| Licences and service plans | `GET /v1.0/subscribedSkus` | `Directory.Read.All` (have) |
| Per-user plan status (adoption, phase 3) | `GET /v1.0/users/{id}/licenseDetails` | `Directory.Read.All` (have) **[verify]** |
| Conditional Access present and enabled, legacy auth blocked | `GET /v1.0/identity/conditionalAccess/policies` | `Policy.Read.All` (have) |
| Security Defaults state | `GET /v1.0/policies/identitySecurityDefaultsEnforcementPolicy` | `Policy.Read.All` (have) **[verify]** |
| MFA registration coverage | `GET /v1.0/reports/authenticationMethods/userRegistrationDetails` | `AuditLog.Read.All` / `Reports.Read.All` (have) |
| Intune compliance policy present | `GET /v1.0/deviceManagement/deviceCompliancePolicies` | `DeviceManagementConfiguration.Read.All`, **new** **[verify]** |
| Secure Score control IDs and states | `GET /v1.0/security/secureScoreControlProfiles` | `SecurityEvents.Read.All` (have) **[verify]** |
| External sharing level | `GET /v1.0/admin/sharepoint/settings` | `SharePointTenantSettings.Read.All` (have) |

### Exchange Online and Security & Compliance PowerShell (new)

Verified on Microsoft Learn ("App-only authentication in Exchange Online PowerShell and Security &
Compliance PowerShell", 2026-08-26):

- **A certificate is required** for app-only. Client secrets aren't an option.
  - It must have a **CSP** key, not CNG. Modern Windows creates CNG by default, so Vigil365 must
    create it explicitly.
  - Passing a certificate object works on Linux (Docker); a thumbprint works on Windows only.
- **Permissions:** Office 365 Exchange Online › `Exchange.ManageAsApp`, and Microsoft Exchange Online
  Protection › `Exchange.ManageAsApp` (one per environment), with admin consent.
- **Read-only comes from the role, not the permission.**
  - Assign **Global Reader** (supported for both Exchange Online and Security & Compliance).
    `Exchange.ManageAsApp` alone grants nothing; the directory role in the token sets what the session
    can do.
  - Never assign Exchange Administrator, Security Administrator or Global Administrator.
- **Module versions:** ExchangeOnlineManagement 2.0.4 or later for Exchange Online, and 3.0.0 or later
  for Security & Compliance.
- **MSP edition:**
  - Microsoft documents the multitenant pattern for Exchange Online: per-client admin consent (scope
    `https://outlook.office365.com/.default`) **and the role assigned in each client tenant**.
  - Consent alone doesn't assign a directory role, so client onboarding gains a step.
  - Whether Security & Compliance supports the same multitenant app-only pattern is **[verify]**.

Planned reads (cmdlet names **[verify]** against the ExchangePowerShell reference before use):

| Check | Cmdlet (Get-* only) | Session |
| --- | --- | --- |
| Safe Links scoped and on | `Get-SafeLinksPolicy`, `Get-SafeLinksRule` | Exchange Online |
| Safe Attachments | `Get-SafeAttachmentPolicy`, `Get-SafeAttachmentRule` | Exchange Online |
| Preset security policies | `Get-EOPProtectionPolicyRule`, `Get-ATPProtectionPolicyRule` | Exchange Online |
| Audit log ingestion on | `Get-AdminAuditLogConfig` | Exchange Online |
| DLP policy present | `Get-DlpCompliancePolicy` | Security & Compliance |
| Retention policy present | `Get-RetentionCompliancePolicy` | Security & Compliance |

**Runtime:**
- A shipped, reviewed script `posture-collect.ps1` runs in a `pwsh` 7 child process for each tenant
  and writes JSON to stdout. The API never hosts PowerShell in-process.
- A test fails if the script contains any cmdlet that isn't `Get-*`, `Connect-ExchangeOnline`,
  `Connect-IPPSSession` or `Disconnect-ExchangeOnline`. The same "only reads" guard Vigil365 applies
  to Graph POSTs.
- If `pwsh`, the module or the certificate is missing, every Exchange and Purview check reports
  `Unknown` with that reason. Nothing else is affected.

**Installer:**
- Detect or install PowerShell 7 and a pinned ExchangeOnlineManagement version. Installing from
  PowerShell Gallery needs internet at setup, as Azure CLI already does.
- Create a CSP certificate for the app registration, upload its public part to the app, and store
  the private key like existing Graph certificate credentials.
- Request both `Exchange.ManageAsApp` permissions, and assign **Global Reader** to the app.

## 5. Entitlement map source

- Microsoft's "Product names and service plan identifiers for licensing" CSV:
  `https://download.microsoft.com/download/e/3/e/e3e9faf2-f28b-490a-9ada-c6089a1fc5b0/Product%20names%20and%20service%20plan%20identifiers%20for%20licensing.csv`
  (1,134,334 bytes, Last-Modified 2026-08-19).
- The on-page table's columns are "Product name", "String ID", "GUID", "Service plans included" and
  the friendly names. The CSV header is **[verify]** on download.
- `scripts/update-entitlements.ps1` downloads it, keeps the Business Premium, E3 and E5 rows, and
  writes `entitlements.json` with the source URL and retrieval date on every row.
- "Feature X needs service plan Y" (for example Conditional Access needs Microsoft Entra ID P1) is
  sourced per row from the feature's own Microsoft documentation, never assumed.

## 6. Baselines and licensing

- **SCuBA:** IDs in the form `MS.AAD.1.1v1` from CISA's ScubaGear baselines. Reuse terms
  (US-government work) **[verify]** in the ScubaGear repository licence.
- **Secure Score:** control IDs come from Graph at runtime, so no third-party text.
- **CIS:** the CIS Microsoft 365 Foundations Benchmark appears to be under a non-commercial licence
  **[verify]**, and Vigil365 is MIT and used commercially by MSPs. Until CIS's terms are confirmed:
  - store **recommendation numbers only**, with titles in our own words
  - never copy CIS benchmark text, rationale or audit steps into the repo
  - if the terms don't allow even that, the CIS column becomes an optional mapping file the operator
    supplies

## 7. Phases

| Phase | Scope |
| --- | --- |
| **1. Pipeline on Graph** | Data schemas and tests; `update-entitlements.ps1` and Business Premium/E3/E5 entitlements; licence collector (service plans); rules engine with all statuses; Graph checks (CA, legacy auth, Security Defaults, MFA registration, Intune compliance policy, external sharing); Secure Score and SCuBA cross-map; Posture page; HTML and CSV export with sanitised fixtures. |
| **2. Exchange and Purview** | `posture-collect.ps1` and the pwsh runner; installer steps (pwsh, module, CSP certificate, permissions, Global Reader); MSP onboarding step for the role; Safe Links, Safe Attachments, presets, audit log, DLP and retention checks; the "only reads" script guard. |
| **3. Adoption and imports** | `LicensedLowAdoption` from usage reports and per-user plan status; Maester and ScubaGear JSON import; upgrade-gated pre-sales view. |

Not in any phase:
- write or remediation actions
- cost or currency figures

## 8. Changes outside the module

- `graph-permissions.json`: add `DeviceManagementConfiguration.Read.All` **[verify]**. Every client
  re-consents once.
- Exchange and Purview permissions aren't Graph. Document them in a new "Exchange and Purview"
  section of `docs/graph-permissions.md` rather than a separate `PERMISSIONS.md`, so Vigil365 keeps
  one permission reference.
- **Public wording:** "only reads from Microsoft Graph" becomes "only reads from Microsoft 365".
  Explain why `Exchange.ManageAsApp` is read-only under Global Reader.

## 9. Needed from Samir before phase 1 starts

1. **Permission to download the Microsoft licensing CSV** above (about 1.1 MB, from
   download.microsoft.com) to seed the entitlement map.
2. Confirmation that **Global Reader** is acceptable for the app in each tenant. It's read-only, but
   it's a directory role the app currently doesn't have.
3. **The CIS licence question** in §6. Whoever owns licensing at Infrassist should check CIS's terms.
