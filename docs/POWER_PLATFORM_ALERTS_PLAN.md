# Power Platform page: alerts, not inventory — plan

Status: **built on 2026-11-07 (sections 3 to 5), not yet checked on a live tenant.** Builds on phase A of
`COPILOT_POWER_PLATFORM_PLAN.md` (Purview audit alerts, built, not yet seen on a live tenant). Items marked
*assumed* need a live check; section 6 is still open.

**Owner's asks:**
- 2026-11-06: "in power platform i want alerts not inventory."
- 2026-11-07: no changes in the customer's environment. Consent is fine.

Flow run errors are a separate, parked plan (`FLOW_ERRORS_PLAN.md`). This page leaves room for them but
doesn't depend on them.

## 1. What changes for the operator

- The **Power Platform** page shows **alerts and activity** for Copilot Studio agents, Power Automate, Power
  Apps and Power Platform admin changes. It no longer reads the inventory.
- No Microsoft sign-in popup on the page any more. Everything comes from what Vigil365 already collects in
  the background, from the Purview audit log, with Vigil365's own read-only permission.
- More admin changes are caught: DLP policy deleted, sharing limits changed, an admin taking System
  Administrator in an environment, an environment's security group changed, and others (section 3).
- Bulk deletion of flows or apps raises an alert.

## 2. Why the inventory goes

- It can't alert. Microsoft lets only a signed-in person read it, so it only runs while someone has the page
  open.
- It brings the most risk on the page: an MSAL popup, delegated Power Platform scopes, a "Register the Power
  Platform API" step with `Application.ReadWrite.All` (a write permission), two extra origins in the CSP, and
  owner look-ups.
- The alerts already cover the same risks as they happen: agent shared or authentication changed, flow
  using a high-risk connector or shared outside the organisation, app consent bypass, and so on.

What is lost: a point-in-time list of everything that exists, including things made before Vigil365 was
installed. If the owner wants that back later, it can return as a separate, optional page.

## 3. Alerts: fixes and additions

### 3a. Fix: admin events Vigil365 probably never fetches

- **The gap:** Microsoft now logs DLP policy changes, environment groups and their rules (sharing limits for
  apps and flows), "admin took System Administrator" and Customer Lockbox under the record type
  `PowerPlatformAdministratorActivity`. That type is **not in Graph's `auditLogRecordType` list**, and the
  Power Platform audit search asks by record type only (`PurviewAuditCollector.cs:141`).
- **Fix:** a third audit search, `powerPlatformAdmin`, that asks by **operation name** instead. Microsoft
  documents these names:
  - DLP: `GovernanceApiPolicyOperation`
  - Environment groups: `NewEnvironmentGroup`, `UpdateEnvironmentGroup`, `DeleteEnvironmentGroup`,
    `EnvironmentAddedToEnvironmentGroup`, `EnvironmentRemovedFromEnvironmentGroup`
  - Their rules: `CreateRuleSetOperation`, `UpdateRuleSetOperation`, `DeleteRuleSetOperation`,
    `CreateRuleBasedPolicyOperation`, `UpdateRuleBasedPolicyOperation`, `DeleteRuleBasedPolicyOperation`,
    `CreateRuleBasedPolicyAssignmentOperation`, `DeleteRuleBasedPolicyAssignmentOperation`
  - Admin access: `ApplyAdminRole`, `LockboxRequestOperation`
- It uses the existing state machine. `PurviewAuditState` is keyed by feed, so there is **no migration**; the
  status endpoint and panel list the new feed.
- *Assumed:* that Graph returns records of that type when asked by operation. If it doesn't, the fallback is
  the Office 365 Management Activity API, which is a new permission (`ActivityFeed.Read`) and needs the
  owner's OK.
- **Normaliser:** these records keep their detail in `PropertyCollection` (for example
  `…governance.api_policy.operation_name` = `DeleteDlpPolicy`, and the resource's display name). Map them:

| Records | Activity name written | Notes |
| --- | --- | --- |
| DLP, operation name contains Delete | `Power Platform DLP policy deleted (<policy>)` | |
| DLP, Update | `Power Platform DLP policy changed (<policy>)` | Matches the existing rule. |
| DLP, Create | `Power Platform DLP policy created (<policy>)` | |
| DLP connector-blocking, custom connector patterns, connector configurations | `Power Platform connector policy changed (<operation>)` | |
| Rule set / rule-based policy operations | `Environment group rules changed (<operation>)` | Sharing limits live here. Fold the rule name in if the record has it (*assumed*). |
| Environment group operations | `Environment group changed (<operation>)` | |
| `ApplyAdminRole` | `Admin took System Administrator in an environment` | |
| `LockboxRequestOperation` | `Customer Lockbox request (<state>)` | |

- **The existing environment search gets sharper.** "Changed property on environment" that touches the
  security group becomes `Environment security group changed`. Copied, restored, moved and converted become
  `Environment copied`, `Environment restored` and so on. Microsoft documents only friendly names for these,
  so matching is by keyword, like Power Apps today (*assumed*).

### 3b. New rules

They are added to the `powerplatform` pack. The pack is seeded once per install
(`AlertingSchema.SeedDefaultPolicies`), so existing installs need a **one-time top-up by name**. A deleted rule isn't added
back unless all of them are deleted, as with the first pack (no marker is stored; that would need a migration).

| Rule | Pattern | Window, threshold | Severity | Default |
| --- | --- | --- | --- | --- |
| Power Platform DLP policy deleted | `Power Platform DLP policy deleted*` | 60 min, 1 | High | On |
| Power Platform connector policy changed | `Power Platform connector policy changed*` | 60 min, 1 | High | On |
| Sharing limits or environment group rules changed | `Environment group rules changed*` | 60 min, 1 | Medium | On |
| Environment security group changed | `Environment security group changed*` | 60 min, 1 | High | On |
| Admin took System Administrator in an environment | `Admin took System Administrator*` | 60 min, 1 | Medium | On |
| Environment copied or restored | `Environment copied*`, `Environment restored*` (two rules) | 60 min, 1 | Medium | On |
| Customer Lockbox request | `Customer Lockbox request*` | 60 min, 1 | Medium | On |
| App conditional access changed | `App conditional access changed*` (already written, no rule yet) | 60 min, 1 | Medium | On |
| Many flows deleted | `Flow deleted*` | 60 min, **5** | High | On |
| Many apps deleted | `App deleted*` | 60 min, **5** | High | On |
| Power Platform DLP policy created | `Power Platform DLP policy created*` | 60 min, 1 | Low | Off |
| Environment group changed | `Environment group changed*` | 60 min, 1 | Low | Off |

- Bulk-deletion rules count across everyone. Records still enter the window when Vigil365 collects them, but
  a rule with a threshold above 1 needs that many to have happened within its window
  (`AlertEvaluator.MostWithinWindow`), so a day of records arriving at once isn't a burst.
- **Later, not in this plan** (they need correlation across sources, a small engine change):
  - "Flow created or edited by a disabled account" (Entra account state × flow events).
  - "Flow shared with a guest invited in the last 7 days" (Entra "Invite external user" × flow sharing).

## 4. The page

Same nav entry and route (`#/powerplatform`). Data comes from what the app already loads, plus one small
endpoint change.

- **Status line** (reuse `PurviewAuditPanel`): each feed's state, last read, "Needs permission" with what to
  grant, and a fixed note: "Microsoft's audit log can take up to 24 hours to show an action."
- **Tiles:**
  - Open alerts (new and acknowledged) in the `powerplatform` and `copilot` categories
  - Of those, high
  - Events in the last 7 days
  - Rules on
- **Open alerts** (from the `triggeredAlerts` the app already loads, filtered by category):
  - Columns: severity, rule, product, triggered, status, assignee.
  - Each row opens the alert in the Alert Center (the existing `deepLinkAlertId`), where acknowledge,
    resolve, snooze and assign already work. Nothing is duplicated.
  - Search, filter (severity, status, product), sort, CSV.
- **Activity** (`GET /api/audit-events`):
  - Columns: when, product, activity, who, target, result.
  - Each row shows a badge when an enabled rule's pattern matches its activity ("Would alert: Flow shared
    outside the organisation"), matched in the browser with the same `*` rules as the server.
  - Search, filter (product, "would alert" only, days 7/30/90), sort, CSV.
  - One endpoint change: `source=purviewAudit` returns all four products. Today it takes one product or
    `entra`.
  - **Not built:** the record's detail (`RawJson`) on demand. Tenant Activity has no such view either and no
    route returns it; it would need a new route, Analyst or above, because raw records hold names and addresses.
- **Rules:**
  - The `powerplatform` and `copilot` rules: on/off, severity, last triggered, count.
  - "Edit in Alert Center" link. Admins change rules there, as now.
- **Empty states say what to do next:**
  - Permission not granted: grant `AuditLogsQuery.Read.All`.
  - Waiting for the first search: the status line above the tables says "no results yet".
  - Nothing happened in the period.

**`/api/triggered-alerts` caveat:** it returns the newest 500 across all categories. If that hides
Power Platform alerts on a busy tenant, add an optional `category` filter. That is a query parameter on an
existing route, so its access is unchanged.

## 5. Removing the inventory

**Server**
- `Endpoints/PowerPlatformEndpoints.cs`: remove `GET /api/power-platform/settings` and
  `POST /api/power-platform/inventory/evaluate` (and `app.MapPowerPlatformEndpoints()` if nothing is left).
- Delete `Services/PowerPlatform/PowerPlatformInventory.cs`, `PowerPlatformOwners.cs`, and its DI line in
  `Program.cs`.
- `Program.cs` CSP: `connect-src` goes back to `'self'`, the sign-in authority and `wss:`. The Graph and Power
  Platform API origins were only there for the inventory and its register step. Nothing else in the browser
  calls Graph (checked: only `services/powerPlatform.ts`).
- `GraphOptions`: remove `PowerPlatformApiBaseUrl`, `PowerPlatformMaxEnvironments` and
  `PowerPlatformWideSharingUsers`. Keep `PowerPlatformHighRiskConnectors`: the audit normaliser uses it.
- Tests:
  - Delete `PowerPlatformInventoryTests`, `PowerPlatformEvaluateEndpointTests` and the owner-access tests.
  - `RouteAuthorizationTests`: drop the two routes.

**Client**
- Delete `services/powerPlatform.ts` and `services/msalPopup.ts`, with their tests, and the popup hand-back
  in `main.tsx`.
- `acquireTokenPopupForTenant` and `acquireTokenSilentForTenant` in `api.ts`: delete if nothing else uses
  them (only `powerPlatform.ts` does today).
- Rewrite `PowerPlatformPage.tsx` and its test; update `PageHelp` text.
- e2e: rewrite the Power Platform part of `copilot-power-platform.spec.ts`, plus the `a11y.spec.ts` stubs and
  fixtures.

**Docs**
- `THREAT_MODEL.md`: remove the in-browser delegated read; add the third audit search.
- `graph-permissions.md`: remove the delegated Power Platform scopes and the register step.
- `SECURITY.md`, `README.md` security section.
- `CHANGELOG.md` (Unreleased): "Removed: Power Platform inventory. Added: Power Platform alerts page, admin
  change alerts, bulk deletion alerts."
- `COPILOT_POWER_PLATFORM_PLAN.md`: phase D marked removed.
- The public site (`docs/index.html`, `llms.txt`) if it mentions the inventory.

**In tenants that used it** (nothing for Vigil365 to do; say so in the changelog):
- Consent for the delegated Power Platform scopes stays until an admin removes it.
- The Power Platform API service principal the register step created is Microsoft's own and harmless.
- On the owner's tenant, the old Power Platform reader role assignment from 2026-10-22 still exists and can
  be removed.

## 6. Check on a live tenant (owner's tenant) — open

1. **Grant `AuditLogsQuery.Read.All`** (consent only, read-only). Without it the page is empty. On
   2026-10-19 it was not granted on the owner's install.
2. Make a few harmless admin changes in a test window: create and delete a test DLP policy, change an
   environment group rule, request System Administrator in an environment. Then confirm:
   - the `powerPlatformAdmin` search returns them;
   - which `Operation`, record type and `PropertyCollection` keys they carry.

   Adjust the normaliser.
3. Capture the real operation strings for flow and app events (still open from phase A §6) and check the
   "skipped operations" log line.
4. How long records take to arrive, which sets the empty-state wording.

## 7. Effort

| Part | Days |
| --- | --- |
| 3a fix (third audit search, normaliser, tests) | 1–1.5 |
| 3b rules and top-up seed | 0.5 |
| Page (section 4), vitest, e2e | 1.5–2 |
| Inventory removal (section 5) | 0.5–1 |
| Docs | 0.5 |
| **Total** | **4–5.5**, plus the live check |

## 8. Decisions for the owner

1. **Copilot Studio agent alerts on this page.** Recommended: yes. Agents are built in Power Platform; the
   Copilot page stays about Copilot usage and licences.
2. **Act on alerts here or in the Alert Center.** Recommended: open them in the Alert Center, so there is one
   place for acknowledge, resolve, snooze and assign.
3. **Default on/off for the new rules** (section 3b). Recommended as listed.
4. **Fallback if Graph can't return the admin record type:** the Office 365 Management Activity API. That is a
   new permission (`ActivityFeed.Read`) and a second audit pipeline. Recommended: decide only if the live
   check shows it's needed.

## 9. Sources (checked 2026-11-06)

- [Power Platform admin activity logs](https://learn.microsoft.com/en-us/power-platform/admin/admin-activity-logging)
- [Power Automate activity logs](https://learn.microsoft.com/en-us/power-platform/admin/logging-power-automate)
- [Power Apps activity logs](https://learn.microsoft.com/en-us/power-platform/admin/logging-powerapps)
- [Graph auditLogRecordType](https://learn.microsoft.com/en-us/graph/api/resources/security-auditlogrecordtype?view=graph-rest-1.0)
- [Management Activity API record types](https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#auditlogrecordtype)
- [Create auditLogQuery (operationFilters)](https://learn.microsoft.com/en-us/graph/api/security-auditcoreroot-post-auditlogqueries?view=graph-rest-1.0)
- [Sentinel Power Platform rules (detection ideas)](https://learn.microsoft.com/en-us/azure/sentinel/business-applications/power-platform-solution-security-content)
