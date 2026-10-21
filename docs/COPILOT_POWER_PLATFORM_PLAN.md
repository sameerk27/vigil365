# Copilot and Power Platform: plan

Status: **phases A and B built on `feat/copilot-power-platform` (2026-10-19); phase D built as an opt-in option (2026-10-21); none yet checked against a live tenant; phase C waits for a licensed tenant** (plan revised 2026-10-18, phase D revised 2026-10-21). Microsoft's APIs and audit schemas were
checked on that date; re-check section 2 before building, because some of the sources are beta or
preview.

**Priority (owner, 2026-10-18):** alerts on **Copilot agents**, **Power Automate flows** and
**Power Apps**. The Copilot usage page comes second.

## 1. What an operator should get

- An alert when someone creates, publishes or shares a Copilot agent, changes its authentication, or
  gives it a new action.
- An alert when a flow is created or edited with a high-risk connector, or shared with a new owner,
  especially someone outside the organisation.
- An alert when an app is shared or published, or an admin weakens a Power Apps safeguard (consent
  bypass, third-party apps, quarantine).
- All of it read-only. Nothing in this plan changes a setting in a tenant.

## 2. What Microsoft exposes

### Audit events (the source for every alert in section 3)

All of these land in the Microsoft Purview audit log. Vigil365 reads that log through Graph:
`POST /v1.0/security/auditLog/queries`, then `GET /v1.0/security/auditLog/queries/{id}/records`.
This needs **`AuditLogsQuery.Read.All`**, a new read-only application permission. The API is v1.0 and
generally available. It's asynchronous, and tenant-level daily and concurrent query limits apply.

| Product | Events Microsoft documents | Useful fields | Prerequisite |
| --- | --- | --- | --- |
| Copilot Studio agents | `BotCreate`, `BotDelete`, `BotUpdateOperation-BotPublish`, `BotUpdateOperation-BotShare`, `BotUpdateOperation-BotAuthUpdate`, `BotUpdateOperation-BotNameUpdate`, `AIPluginOperationCreate/Update/Delete`, `BotComponentCreate/Update/Delete`, `EnvironmentVariableCreate/Update/Delete`, and `CopilotInteraction` for usage | `BotId`, `BotSchemaName`, `BotUpdateDetails`, `AIPluginOperationName`, user, time | Admin activities are logged **by default** and can't be turned off at the source. Users need a Microsoft 365 licence. Not FedRAMP tenants. |
| Power Automate (cloud flows) | Created flow, Edited flow, Deleted flow, Edited permissions, Deleted permissions, paid-trial events | **`FlowConnectorNames`** (connectors in the flow at create or edit time), **`RecipientUPN`**, `SharingPermission` (3 = owner, 2 = run-only), `UserTypeInitiated` (1 = user, 2 = admin), `FlowDetailsUrl` | Docs list per-environment auditing as a prerequisite. **No flow runs, failures or runtime connector calls**; those need Application Insights or Dataverse. Desktop flows are in Dataverse audit only. |
| Power Apps (canvas) | Created, Edited, Published, Deleted app; Edited and Deleted app permission; Consented to the app's APIs; Imported canvas app; Admin modified app owner or permissions; Admin set bypass consent state; Admin set quarantine state; Admin allowed third-party apps; Admin set conditional access; DLP evaluation result | `PropertyCollection`: app display name and id, environment name and id, `powerplatform.analytics.activity.name` (for example `AdminDeleteApp`), user, role (Admin) | Docs list per-environment auditing as a prerequisite. |
| Power Platform admin | DLP policy and environment events (record types `powerPlatformAdminDlp`, `powerPlatformAdminEnvironment`) | Per record | None beyond auditing being on. |

**Exact operation strings:** Copilot Studio documents its event labels, and they appear in
`Operation`. For Power Automate and Power Apps, Microsoft documents friendly names ("Created flow"),
not the `Operation` strings. Capture those from a live tenant before writing templates.

### Other sources

| Data | API | Permission | Status | Notes |
| --- | --- | --- | --- | --- |
| Defender AI agent inventory (Copilot Studio, Foundry and others) | Advanced hunting, `AgentsInfo` table (`AIAgentsInfo` was retired 2026-07-01) | `ThreatHunting.Read.All`, **already granted** | GA, licence-gated | Needs an Agent 365 licence, or Defender for Cloud Apps plus Copilot Studio. A Power Platform admin has to switch it on. |
| Defender runtime protection for Copilot Studio agents (for example, blocked prompt injection) | Defender alerts, already collected by the "Defender alerts" source (`/v1.0/security/alerts_v2`) | `SecurityAlert.Read.All`, **already granted** | **Preview** | Unverified how these alerts identify themselves. Check on a licensed tenant, then tag them as agent alerts. |
| Copilot usage per user (last activity per app) | `GET /beta/reports/getMicrosoft365CopilotUsageUserDetail(period='D28')` | `Reports.Read.All`, **already granted** | Beta | Licensed users only. Names are hashed if the tenant conceals report names. Microsoft points to the newer `/copilot` report path. |
| Copilot seats bought and assigned | `GET /v1.0/subscribedSkus` | `Directory.Read.All`, **already granted** | v1.0 | Already read by the licences endpoint. Confirm the Copilot SKU part number on a real tenant. |
| Power Platform inventory (environments, apps, flows) | `api.powerplatform.com`, api-version `2024-10-01`: `GET /environmentmanagement/environments`, `GET /powerapps/environments/{id}/apps` (as administrator), `GET /powerautomate/environments/{id}/cloudFlows` | **Power Platform reader** RBAC role (`c886ad2e-27f7-4874-8381-5849b8d8a090`, "all permissions that end with .Read") at tenant scope, per tenant | Role **preview**; endpoints documented | Not Graph and not granted by admin consent. A Power Platform admin assigns it in each tenant (`POST /authorization/roleAssignments`). App-only token: client credentials, scope `https://api.powerplatform.com/.default`; Power Platform API uses no application permissions, the RBAC role decides access. **Built as an opt-in option (phase D).** Rechecked 2026-10-21. |
| (Rejected) Power Platform inventory API (Azure Resource Graph, `POST /resourcequery/resources/query`) | `api.powerplatform.com` | Delegated user only | — | Microsoft: app-only identities get 403. Covers agents too, but Vigil365 collects as an app. |
| (Rejected) Power Platform admin (BAP) API via management-app registration | `api.bap.microsoft.com` | The app is treated as a Power Platform Administrator | GA | **Breaks the read-only promise. Do not use.** |

## 3. Alert catalogue

Default severities are proposals. The trigger column uses the documented event names; replace them
with the live `Operation` strings during build. "Detail rule" means the collector folds a condition
into the activity name at ingest (section 4).

### Copilot agents (Copilot Studio)

| Alert | Trigger | Severity | Notes |
| --- | --- | --- | --- |
| Agent authentication changed | `BotUpdateOperation-BotAuthUpdate` | High | Can open an agent to anyone. Show `BotUpdateDetails` in the alert. |
| Agent shared | `BotUpdateOperation-BotShare` | Medium | Raise to high if `BotUpdateDetails` shows broad sharing (verify the field contents live). |
| Agent published | `BotUpdateOperation-BotPublish` | Medium | |
| Agent given a new action or plugin | `AIPluginOperationCreate`, `AIPluginOperationUpdate` | Medium | Gives the agent a way to call other systems. |
| Agent created | `BotCreate` | Low | Off by default; noisy in busy tenants. |
| Agent deleted | `BotDelete` | Low | |
| Defender blocked an agent action | Defender alert (runtime protection) | From Defender | Preview; only with the licences above. |

### Power Automate flows

| Alert | Trigger | Severity | Notes |
| --- | --- | --- | --- |
| Flow uses a high-risk connector | Created or Edited flow **and** `FlowConnectorNames` contains a listed connector (detail rule) | High | Configurable list. Starting proposal: HTTP, HTTP with Microsoft Entra ID, custom connectors, FTP/SFTP, and consumer services such as Gmail and Dropbox. |
| Flow shared with someone outside the organisation | Edited permissions **and** `RecipientUPN` is a guest or not a verified domain (detail rule) | High | Verified domains via `GET /v1.0/domains` (`Directory.Read.All`, already granted). |
| Flow co-owner added | Edited permissions **and** `SharingPermission` = 3 | Medium | |
| Flow deleted by an admin | Deleted flow **and** `UserTypeInitiated` = 2 | Low | |

### Power Apps

| Alert | Trigger | Severity | Notes |
| --- | --- | --- | --- |
| Consent bypass turned on for an app | Admin set bypass consent state | High | Users stop seeing the connector consent prompt. |
| Third-party apps allowed | Admin allowed third-party apps | High | |
| App permissions changed | Edited app permission, Admin modified app permissions | Medium | Check live whether the record says who it was shared with. If it does, add "shared with everyone" as high. |
| App owner changed by an admin | Admin modified app owner | Medium | |
| App quarantine state changed | Admin set quarantine state | Medium | |
| App published | Published app | Low | |

### Power Platform admin

| Alert | Trigger | Severity |
| --- | --- | --- |
| DLP policy changed | `powerPlatformAdminDlp` events | High |
| Environment created or deleted | `powerPlatformAdminEnvironment` events | Medium |

## 4. How it fits Vigil365

**Collection** (`Services/GraphCollector.cs`): new `CollectPurviewAuditAsync` step, on its own cadence
because of the query limits: `Graph:PurviewAuditIntervalMinutes`, default 60.
- **Run N:** create a query for `[watermark, now]`, filtered by record type or operation to the events
  in section 3.
- **Run N+1:** if the query has `succeeded`, page its records into `AuditEvents` and advance the
  watermark. A run never waits on a query.
- The pending query id and watermark need storage per tenant: a new tenant-scoped row, a migration on
  both SQL Server and PostgreSQL, and an entry in `TenantClassification`.

**Graph client:** `GraphApiClient` only sends GET today, so add a `PostAsync` method.
- **Allow-list it to `/security/auditLog/queries` only**, enforced in code and by a test that fails on
  any other Graph POST. That keeps the read-only claim checkable.

**Normalising events at ingest (no policy-engine change):**
- Activity policies match only on `AuditEvent.Activity` with `*` wildcards
  (`AlertEvaluator.ComputePolicyValueAsync`). So the collector writes a normalised activity name and
  folds detail rules into it:
  - "Flow created (high-risk connector: HTTP)"
  - "Flow shared (external: alex@fabrikam.com)"
  - "Agent authentication changed"
- Pattern `Flow created*` still matches the plain and the risky case, so existing policies,
  suppression rules and affected-entity lists work unchanged.
- The other fields are filled as:
  - `Source = "purviewAudit"`
  - `Category` = product (Copilot Studio, Power Automate, Power Apps, Power Platform admin)
  - `ActorUpn` = the user
  - `TargetName` = the agent, flow or app name
  - `RawJson` = the full record, so the detail view shows `BotUpdateDetails`, `FlowConnectorNames`
    and the rest
- The high-risk connector list is a setting with the defaults above.

**Templates:**
- Add the section 3 alerts to `ActivityDefaults` in `Data/AlertingSchema.cs` under new categories
  (`copilot`, `powerplatform`).
- The seed runs on empty tables, so existing installs need a by-name top-up.
- Low-severity ones ship disabled.

**Permission:** add `AuditLogsQuery.Read.All` to `graph-permissions.json` as **optional**
(recommended):
- Existing installs keep working.
- Collection Health shows "Needs permission" until it's granted.
- Every tenant that has already consented must consent again to get it. In the MSP edition that means
  every client, through the existing consent link.

**Pages:**
- Tenant Activity gets a source filter: Entra ID, Copilot agents, Power Automate, Power Apps,
  Power Platform admin.
- Alert Center shows the new templates under their categories.
- No new page is needed for the alerts.

**Wording that becomes wrong and must change in the same release.** These files say the collector
"only sends read requests (GET)":
- `docs/index.html` (description, FAQ, read-only section)
- `docs/llms.txt`

Change it to "reads only; the one POST starts an audit search and changes nothing in the tenant", and
check `README.md` (security section), `SECURITY.md`, `docs/THREAT_MODEL.md` and
`docs/graph-permissions.md` for the same claim.

**Retention:** `DataRetentionWorker` already prunes `AuditEvents`.

## 5. Phases

**A. Agent, flow and app alerts from the Purview audit log (priority).** Everything in sections 3
and 4. About 4–6 days, including the live capture of operation strings and record shapes.

**B. Copilot usage page.** No permission change.
- `GET /api/dashboard/copilot`, a new `CopilotPage.tsx`, and a nav entry.
- KPI tiles: licensed, active in 30 days, inactive for 30 days, unused seats.
- A per-app chart, a user table with search, filter and export, and a banner when names are hidden.
- An "Unused Copilot licences" recommendation.
- `GraphPermissionCoverageTests.Needs` gets the report path mapped to `Reports.Read.All`.

About 1–2 days.

**C. Defender agent signals.** No permission change.
- An agent inventory from `AgentsInfo` (advanced hunting).
- Defender runtime-protection alerts tagged as agent alerts.

Only useful on tenants with the licences in section 2. About 1–2 days after a licensed tenant confirms
the alert shape.

**D. Power Platform inventory (built as an option, 2026-10-21).** The owner chose not to wait for
the reader role to reach general availability: each tenant opts in.
- **Turning it on:** `grant-power-platform-reader.ps1 -TenantId -AppId` (repository root, Az.Accounts),
  run by that tenant's Power Platform administrator. It finds Vigil365's service principal, registers
  Microsoft's Power Platform API service principal (`8578e004-a5c6-46e7-913e-12f58912df43`) if the tenant
  lacks it, and assigns the reader role at `/tenants/{id}`. `-Remove` deletes the assignment; `-WhatIf`
  changes nothing. Pester tests: `scripts/tests/grant-power-platform-reader.Tests.ps1`.
- **Reading:** `PowerPlatformApiClient` (GET only, same host only, same app credentials as Graph) and
  `PowerPlatformInventoryService` (`Services/PowerPlatform/`). Read when the page opens, cached 15 minutes
  per tenant, no background collection and no database tables. At most `Graph:PowerPlatformMaxEnvironments`
  (50) environments, four at a time; one failing environment is reported, the rest still read.
- **States:** `NotEnabled` (403/401, or AADSTS500011 when the tenant has no Power Platform API service
  principal) shows the turn-on steps with the tenant's command filled in; `Ready`; `Partial`; `Error`.
- **Flags:** apps that bypass consent (high), use a high-risk connector (high, the same
  `Graph:PowerPlatformHighRiskConnectors` list as phase A), a custom connector or the on-premises gateway
  (medium), are shared with `Graph:PowerPlatformWideSharingUsers` (25) people or more (medium) or with a
  group (low); flows with a high-risk connector (high) or suspended (low).
- **Page:** Power Platform (own sidebar entry): tiles, apps and flows tables with search, filters and CSV
  export, environments table. `GET /api/power-platform/inventory[?refresh=true]`.
- **Not in it:** Copilot Studio agents (no app-only list in Power Platform API; the inventory API that has
  them is delegated only, so agents stay with phase C), DLP policies, alerts on inventory changes.

## 6. Must be checked on a live tenant before building

- Run one audit query by hand (Graph Explorer) covering a created, shared and published agent, a flow
  created with an HTTP connector, a flow shared with a guest, and an app shared and published. Record:
  - the `Operation` strings
  - the record types
  - where `FlowConnectorNames`, `RecipientUPN` and `BotUpdateDetails` sit in the record
  - whether app-permission records name the recipient
- Whether per-environment auditing must be on for flow and app events in that tenant.
- The delay between an action and its audit record, which sets how quickly an alert can fire.
- The audit query limits on that tenant, to confirm the 60-minute default.
- For phase C: a tenant with Agent 365, or Defender for Cloud Apps plus Copilot Studio.
- For phase D, after running the grant script on a test tenant:
  - that the reader role lets the app call all three endpoints (the docs say RBAC coverage of Power
    Platform API is still growing), including "Get AdminApps", and how long a new assignment takes;
  - the cloud-flow response: the REST reference gives no schema, so the fields (`workflowId`, `name`,
    `stateCode`, `createdOn`, `modifiedOn`, `definition`) follow the .NET SDK's `CloudFlow` model, and
    connectors are read from `properties.connectionReferences` in `definition`. Confirm both;
  - whether `connectionReferences` on an app is an array (as documented) or an object (both are read);
  - that the environment `id` is the value the apps and flows paths take (the apps reference calls it the
    environment's "name field");
  - sovereign clouds: `Graph:PowerPlatformApiBaseUrl` defaults to the commercial host only.

## 7. Decisions for the owner

1. **New permission optional or required.** Recommended: optional.
2. **High-risk connector list.** The proposal in section 3, editable per install.
3. **Release.** Recommended: phase A in 1.3.0, so 1.2.0 (built, not yet tagged) is not delayed.
4. **Noisy alerts.** Recommended: "Agent created" and "App published" ship disabled.

## 8. Sources (checked 2026-10-18)

- [Copilot Studio audit logs in Purview](https://learn.microsoft.com/en-us/microsoft-copilot-studio/admin-logging-copilot-studio)
- [Power Automate activity logs in Purview](https://learn.microsoft.com/en-us/power-platform/admin/logging-power-automate)
- [Power Apps activity logs in Purview](https://learn.microsoft.com/en-us/power-platform/admin/logging-powerapps)
- [Create auditLogQuery (Graph v1.0)](https://learn.microsoft.com/en-us/graph/api/security-auditcoreroot-post-auditlogqueries?view=graph-rest-1.0)
- [Defender AI agent inventory](https://learn.microsoft.com/en-us/defender-xdr/ai-agent-inventory)
- [Copilot Studio agent protection (training module)](https://learn.microsoft.com/en-in/training/modules/enable-protection-copilot-studio-agents/2-explore-copilot-studio-ai-agent-protection)
- [Copilot usage user detail (Graph beta)](https://learn.microsoft.com/en-us/graph/api/reportroot-getmicrosoft365copilotusageuserdetail?view=graph-rest-beta)
- [Assign roles to service principals (Power Platform, preview)](https://learn.microsoft.com/en-us/power-platform/admin/programmability-tutorial-rbac-role-assignment)
- [Power Platform API authentication](https://learn.microsoft.com/en-us/power-platform/admin/programmability-authentication-v2)

Phase D (checked 2026-10-21):
- [Role-based access control for Power Platform (preview)](https://learn.microsoft.com/en-us/power-platform/admin/security/role-based-access-control)
- [Role Based Access Control operations (create, list, delete role assignment)](https://learn.microsoft.com/en-us/rest/api/power-platform/authorization/role-based-access-control)
- [List environments](https://learn.microsoft.com/en-us/rest/api/power-platform/environmentmanagement/environments/list-environments-for-user)
- [Get AdminApps](https://learn.microsoft.com/en-us/rest/api/power-platform/powerapps/apps/get-admin-apps)
- [List cloud flows](https://learn.microsoft.com/en-us/rest/api/power-platform/powerautomate/cloud-flows/list-cloud-flows)
- [CloudFlow model (.NET SDK)](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.management.models.cloudflow?view=power-platform-latest)
- [Power Platform inventory API (delegated only)](https://learn.microsoft.com/en-us/power-platform/admin/inventory-api)
