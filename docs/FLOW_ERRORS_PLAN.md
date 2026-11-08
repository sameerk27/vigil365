# Power Automate flow errors: plan

Status: **parked 2026-11-07: the owner stopped testing before the gate passed.** Route chosen: an admin's
saved, read-only sign-in. Access for other people's flows looks possible, but no run has been read back yet
(section 3). Build only after the gate passes. Microsoft's docs were checked 2026-11-06/07 (section 9). Items marked *assumed* have not
been seen on a live tenant.

**Owner's asks:**
- 2026-11-06: alerts when Power Automate flows fail, on the Power Platform page, which becomes an alerts
  page rather than an inventory (`COPILOT_POWER_PLATFORM_PLAN.md`).
- 2026-11-07: **no changes in the customer's environment.** No solutions, no extra flow steps, no
  application users, no Managed Environments, no Azure exports. Signing in and consenting is fine.
- 2026-11-07: a saved admin sign-in is acceptable **if the test passes**.

## 1. What an operator should get

- An alert when a cloud flow run fails, naming the flow, its environment, its owner and Microsoft's error.
- A stronger alert when the same flow keeps failing, or when Microsoft turns a flow off.
- An alert when Vigil365 stops being able to watch, so silence never looks like health.
- A "Flow errors" section on the Power Platform page: which flows are failing, how often, since when.
- Read-only. Vigil365 never turns a flow on or off, resubmits or cancels a run, or edits a flow.

## 2. Why this route

Microsoft only gives run history to **people** (flow owners and admins), not to apps, unless the customer
sets something up. With the owner's rule, every route that needs setup is out (section 7). What's left is an
admin's own sign-in, used the way Power Automate and Microsoft's PowerShell (`Get-FlowRun`) use it.

- **The API:** `api.flow.microsoft.com`, the service behind the Power Automate portal and Microsoft's
  PowerShell modules. Microsoft doesn't document it for other products and says it is used at your own
  risk. Vigil365 must read it tolerantly and degrade with a plain note when it changes.
- **Why a saved sign-in:** only a sign-in that Vigil365 keeps can check every 15 minutes when nobody has the
  page open. Without it there are no alerts, only a view when someone looks.

## 3. The test (gate: nothing is built until it passes)

Run in **Windows PowerShell 5.1**, signed in as the admin who would connect Vigil365 (Power Platform
Administrator or Global Administrator):

```powershell
Install-Module Microsoft.PowerApps.Administration.PowerShell, Microsoft.PowerApps.PowerShell -Scope CurrentUser -AllowClobber
Add-PowerAppsAccount
Get-AdminFlow | ForEach-Object { Get-FlowRun -FlowName $_.FlowName -EnvironmentName $_.EnvironmentName | Select-Object -First 3 @{n='Flow';e={$_.FlowName}}, Status, StartTime }
```

**Passes** if runs are listed for flows owned by **someone else** (on the owner's tenant:
two flows owned by colleagues), not only the admin's own.

**Also record** (needed for the build, can be read from `Get-FlowRun | Format-List *` on a failed run):
- the run fields: status, error code, error message, start and end time, trigger;
- whether `Get-AdminFlow` shows each flow's state (on, off, suspended);
- whether a Global Reader account gets the same result (a weaker role to connect with).

If it fails, flow errors aren't possible without a change in the customer's tenant. The page then says so
in one line, and this plan stops.

### Results on the owner's tenant (2026-11-07, Global Administrator, Windows PowerShell 5.1)

- `Get-AdminFlow` listed **4** flows. The inventory shows 5: one flow owned by a colleague is
  missing. Find out why before building.
- **`Get-FlowRun` is the wrong test.** It calls the user route and hides errors, so it showed 0 runs
  everywhere. Called directly with the module's token:

  | Flow | Owner | User route `environments/{env}/flows/{flow}/runs` | Admin route `scopes/admin/environments/{env}/flows/{flow}/runs` |
  | --- | --- | --- | --- |
  | Button -> List folder | the admin | 200, 0 runs | 200, 0 runs |
  | Button -> List folder, …, HTTP (state `Suspended`) | the admin | 200, 0 runs | 200, 0 runs |
  | A flow triggered by a new file | a colleague | **403** | **200**, 0 runs |
  | An approval flow | a colleague | **403** | **200**, 0 runs |

- **So far:** the admin route accepts an admin for flows owned by other people; the user route doesn't. No
  flow had a run in Microsoft's 28-day history, so it is **not yet proven that the admin route returns
  runs**.
- **After the owner ran "Button -> List folder" once:** still `200, 0 runs` on every `api.flow.microsoft.com`
  route, including the user route for the admin's own flow (`$top=50`, api-version 2016-11-01 and
  2023-06-01). The Power Platform API routes (`{env}.environment.api.powerplatform.com/powerautomate/flows/{id}/runs`
  and `api.powerplatform.com/powerautomate/environments/{env}/flowRuns`) gave 401 to the PowerShell module's
  token.
- **Power Automate's own page agrees:** the flow's details page says "Your flow hasn't been run yet", so
  the 0 runs were true; the owner's run didn't land on this flow.
- **What the Power Automate page calls** (read from the browser's resource list on the flow's details page;
  ids replaced):
  - runs: `https://{env host}.environment.api.powerplatform.com/powerautomate/flows/{flow}/runs?api-version=1`
  - trigger history: `.../powerautomate/flows/{flow}/triggers/manual/histories?$expand=properties/run/properties&api-version=1`
  - flow health: `.../powerautomate/flows/{flow}/diagnostics?api-version=1&$filter=Trigger,Flow,ConnectionMode,Licensing,LegacyCDSConnector`
  - connections: `.../connectivity/connections?$filter=environment eq '{env}' and ...`
  - Dataverse privilege checks for the signed-in user (`prvReadflowrun`, `prvReadWorkflow`) on the
    environment's Dataverse org: **the owner's Default environment has Dataverse.**

  `{env host}` is the Power Platform API environment host (environment id without dashes, last two
  characters split off as a sub-domain; `default` prefix for the Default environment). That is the
  documented Power Platform API, whose delegated scopes include `PowerAutomate.Flows.Read`, the same API
  the page's existing inventory sign-in already uses. The PowerShell module's token got 401 there, so the
  next test is a token for Vigil365's own app with `PowerAutomate.Flows.Read`, on a flow that has runs.
- **Trigger history** would cover the gap in section 8 (a trigger that fails before a run starts), and the
  **diagnostics** call looks like the page's "fix your flow" checks (connection, licence, trigger). Both are
  *assumed* to be readable the same way; not tested.
- The admin flow list carries `state` (`Started`, `Suspended`), `flowSuspensionReason` and
  `flowFailureAlertSubscribed`, so "Flow turned off" can be read from the same call.
- **Still to check:** these calls used the PowerShell module's own Microsoft app. Vigil365 would sign in
  with its own app registration and `Flows.Read.All`. Whether the admin route accepts that token needs a
  test once the sign-in exists.
- One interactive sign-in failed with "An error occurred while sending the request" and worked on retry.
  Vigil365's connect step must survive that.

## 4. Design (if the test passes)

### Connecting (once per tenant; per client in the MSP edition)

- An Admin clicks **Watch flow errors** on the Power Platform page. Vigil365's **server** runs Microsoft's
  sign-in (authorisation code with PKCE, confidential client), asking only for **`Flows.Read.All`** on
  Power Automate (`https://service.flow.microsoft.com/`) and `offline_access`. *Assumed:* `Flows.Read.All`
  is the read-only delegated permission Power Automate offers; check while building.
- The person signing in must be a Power Platform Administrator or Global Administrator (or Global Reader,
  if the test shows it works). After sign-in, Vigil365 lists flows as admin once; if that fails it keeps
  nothing and says which role is missing.
- Microsoft returns a refresh token. Vigil365 stores the token cache **encrypted** (`SecretProtector`) in a
  new tenant-scoped table `FlowWatch`:
  - `ConnectedUpn`, `ConnectedAt`
  - `Status`: `NotConnected`, `Ok`, `SignInExpired`, `MissingRole`, `Failed`
  - `LastError` (a sentence), `LastRunAt`, `LastSuccessAt`, `Watermark`
  - `TokenCache` (encrypted)

  It needs both migrations and an entry in `TenantClassification`.
- **Disconnect** (Admin) deletes the token cache. Vigil365 can't revoke the sign-in at Microsoft (that
  would be a write); the page says how to revoke it in Entra if needed.
- The redirect URI (`/api/power-platform/flow-watch/callback`) and the delegated permission go on
  Vigil365's own app registration: `register-app.ps1` and the installer in the Single edition, the vendor
  app plus the existing consent link in the MSP edition. That's Vigil365's registration, not the
  customer's Power Platform environment.

**Least privilege, and why it matters here.** A refresh token can be exchanged for **any** delegated
permission consented to the same app, not only the one Vigil365 asks for. So the app this sign-in uses must
carry **no delegated write permission**. Today the inventory's "Register the Power Platform API" step asks
for `Application.ReadWrite.All`. Either that step goes with the inventory (the owner wants alerts, not
inventory), or the flow watcher gets its own app registration with only `Flows.Read.All` (decision 1).

### Reading (`Services/PowerPlatform/FlowWatchCollector.cs`)

- Called from `GraphCollector` like the Purview step, with its own cadence: `Graph:FlowWatchIntervalMinutes`,
  default 15 (0 = off). It has its own status and is never one of the run's source failures.
- **Each run** (paths *assumed* from Microsoft's PowerShell module; check while building):
  1. Get a token silently from the stored cache. On `invalid_grant` (password reset, sessions revoked,
     account disabled, a sign-in frequency policy), set `SignInExpired`, raise "Flow watching stopped" and
     stop.
  2. List environments, then flows as admin:
     `GET https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/scopes/admin/environments/{env}/v2/flows?api-version=2016-11-01`.
     Record each flow's state; a flow that changes to off or suspended raises "Flow turned off".
  3. For each flow, read the newest runs:
     `GET .../providers/Microsoft.ProcessSimple/environments/{env}/flows/{flow}/runs?api-version=2016-11-01`,
     newest first, and stop at the watermark. Keep `Failed` and `TimedOut` (Cancelled is ignored).
  4. Store new failures, then move the watermark.
- **Budget:** each flow costs at least one call per run. `Graph:FlowWatchMaxFlowsPerRun` (default 200) caps it.
  Larger tenants are read in turn, and the page says how long a full pass takes. Throttling (429) backs off to
  the next run. Microsoft's limits for this API are not documented.
- **Client:** `FlowServiceClient`, GET only (a source-scan test like the Graph POST allow-list), fixed
  host `api.flow.microsoft.com` (sovereign hosts only when `Graph:LoginInstance` says so), next links on the
  same host only. The calls are server-side, so the browser's CSP doesn't change.
- **MSAL:** uses `Microsoft.Identity.Client`, which already ships inside `Microsoft.Identity.Web`. A direct
  package reference still needs the owner's OK (decision 2).

### Storage

Failures go into `AuditEvents`, so policies, suppression, affected entities, retention and Tenant Activity work
unchanged:
- `Source = "flowRun"`, `Category = "Power Automate"`, `ExternalId` = run id
- `Activity = "Flow run failed (<error code>)"`, or `"Flow run failed"` when there is no code
- `TargetName` = flow name, `ActorUpn` = flow owner, `OccurredAt` = run end time
- `RawJson`: the selected fields only, error message cut to 1,000 characters

Like Purview events, they count from when Vigil365 collected them (`AlertEvaluator` gets a set of late
sources instead of one).

### Alert rules (category `powerplatform`, seeded once by name)

| Rule | Pattern | Severity | Default |
| --- | --- | --- | --- |
| Flow run failed | `Flow run failed*` | Low | On |
| Flow failing repeatedly | `Flow run failed*`, 3 or more for the same flow in 60 minutes | Medium | On |
| Flow connection broken | `Flow run failed (*Unauthorized*`, `(*Connection*`, `(*Forbidden*` | Medium | On |
| Flow turned off | `Flow turned off*` | Medium | On |
| Flow watching stopped | `Flow watching stopped*` | High | On |

- "Same flow" needs an optional `GroupBy` on activity policies (`target` or `actor`): a new column, with
  both migrations. It also serves bulk-deletion rules later.
- Error codes for a broken connection are *assumed*; adjust to what the test records.

### Endpoints

| Route | Access | Purpose |
| --- | --- | --- |
| `GET /api/power-platform/flow-watch` | Viewer | Status, who it watches as, last read, budget. |
| `POST /api/power-platform/flow-watch/connect` | Admin | Start Microsoft's sign-in; returns the sign-in URL. |
| `GET /api/power-platform/flow-watch/callback` | Anonymous, bound to the Admin's session by `state` + PKCE | Finish sign-in, check the role, store the token cache. |
| `DELETE /api/power-platform/flow-watch` | Admin | Disconnect: delete the token cache. |
| `GET /api/power-platform/flow-errors?days=7` | Viewer | Failing flows grouped by flow. Error messages for Analysts and Admins only. |

All of them go in `RouteAuthorizationTests`; the callback is the only anonymous route and gets its own tests
(wrong state, replay, expired, non-admin session).

### Page

- **Connection card:** "Watching as admin@… since 6 Nov", last read, Disconnect; or Watch flow errors.
- **Tiles:** failed runs in 24 hours, flows failing, flows turned off.
- **Table grouped by flow:** flow, environment, owner, failures (24 hours and 7 days), last error, last
  failure, link to the run in Power Automate. Search, filter, sort and CSV export.
- **Note:** "Read with <name>'s sign-in, read-only. Power Automate's run history is the full record."
- The current line "Vigil365's app has no Power Platform access and nothing is stored" changes.

## 5. Phases

1. **Test (owner, 15 minutes):** section 3.
2. **Backend (3–4 days):** sign-in flow, token cache, collector, client, table, migrations, rules, `GroupBy`,
   endpoints, tests (pure parsers unit-tested; collector against recorded responses; callback security).
3. **Page (1–1.5 days):** card, section, vitest, signed-in Playwright spec with stubbed APIs.
4. **Docs (half a day):**
   - `THREAT_MODEL.md`: stored delegated token, what a stolen one can read, the unsupported API.
   - `graph-permissions.md`: a "Power Automate (not Graph)" section.
   - `CHANGELOG.md`, `PageHelp.tsx`, `README.md` security section.
   - The public site's wording on what Vigil365 stores.

## 6. Decisions for the owner

1. **Keep write permissions off the sign-in app.** Recommended: drop the inventory's "Register the Power
   Platform API" step (`Application.ReadWrite.All`) along with the inventory. The alternative is a second
   app registration just for flow watching.
2. **Direct reference to `Microsoft.Identity.Client`** (already in the build through
   `Microsoft.Identity.Web`). Recommended: yes.
3. **Who sees error messages.** They can contain email addresses or data from the flow. Recommended:
   Analysts and Admins; Viewers see the code.
4. **Polling budget.** Recommended: every 15 minutes, at most 200 flows per run.

## 7. Routes considered and ruled out

| Route | Why not |
| --- | --- |
| Purview audit log | Microsoft logs no flow runs there. |
| Power Platform inventory (the page's current connection) | No run fields; only while the page is open. |
| Dataverse run history (`flowruns`) with an application user | Needs setup in each environment, and only covers flows inside solutions. Owner's rule. |
| Flows write failures to a SharePoint list | Needs every flow edited. Owner's rule. |
| Managed Environment + Application Insights | Premium licences, Azure, admin setup. Owner's rule. |
| Power Platform API `flowRuns` | Needs Dataverse and solution flows; app-only needs Power Platform Administrator rights (403 on 2026-10-22). |
| Vigil365's app as Power Platform Administrator | Declined 2026-10-23; the app could change anything. |
| Power Platform admin center Monitor | No documented API. |
| Read only while the page is open (same API, no saved sign-in) | No alerts. Could be offered on its own if the owner later prefers nothing stored. |

## 8. Not covered

- A trigger that breaks before any run starts (for example, an expired connection on the trigger): no run,
  so nothing to read. The "Flow turned off" rule catches it only once Microsoft turns the flow off.
- Desktop flows and Copilot Studio agent flows (not checked).
- Anything that needs a write.

## 9. Sources (checked 2026-11-06/07)

- [Power Automate activity logs (no runs)](https://learn.microsoft.com/en-us/power-platform/admin/logging-power-automate)
- [Power Platform inventory schema (no run fields)](https://learn.microsoft.com/en-us/power-platform/admin/inventory-schema)
- [Cloud flow run history in Dataverse](https://learn.microsoft.com/en-us/power-automate/dataverse/cloud-flow-run-metadata)
- [Work with cloud flows using code](https://learn.microsoft.com/en-us/power-automate/manage-flows-with-code)
- [Application Insights for Power Automate](https://learn.microsoft.com/en-us/power-platform/admin/app-insights-cloud-flow)
- [List Flow Runs (Power Platform API)](https://learn.microsoft.com/en-us/rest/api/power-platform/powerautomate/flow-runs/list-flow-runs)
- [Power Platform API permission reference](https://learn.microsoft.com/en-us/power-platform/admin/programmability-permission-reference)
- [Monitor Power Automate](https://learn.microsoft.com/en-us/power-platform/admin/monitoring/monitor-power-automate)
- [Guide to cloud flow sharing permissions](https://learn.microsoft.com/en-us/power-automate/guide-to-cloud-flow-sharing-permissions)
- [Community thread: run history via admin PowerShell](https://powerusers.microsoft.com/t5/Power-Apps-Governance-and/Audit-run-data-for-a-Flow/m-p/675183)
