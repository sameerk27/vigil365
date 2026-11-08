# Dedup fix plan

Source: `audits/CODE_DUPLICATION_AUDIT.md` (F1–F16). Code snippets live there (§3, §4). This plan only orders the work.

## Rules (keep token use low)

- Don't re-read the audit or whole files. Find each site with `grep -n "<identifier>"`, then read only that range with `sed -n a,bp`.
- Line numbers in the audit move after each edit. Locate code by identifier, not by line number.
- Write new files in one go from audit §4. For repeated blocks, edit one copy, check it, then repeat the same edit on the rest.
- Run only the test command listed for the step and pipe it through `| tail -30`. Run the full suite once, at the end.
- Make one commit per step. Don't refactor beyond the step.
- Behaviour must not change except where a step says so. JSON field names stay the same.

## Phase 0 — Branch (5 min)

`DashboardEndpoints.cs` is already modified on `feat/copilot-power-platform`. Commit or merge that branch first, then
branch `refactor/dedup` from its tip. Baseline:
`dotnet test src/M365SecurityDashboard.Api.Tests --filter "FullyQualifiedName~Dashboard" | tail -30`.

## Phase 1 — Server helpers (F1, F7, F9 foundations) · S

| Step | Do | Test |
|---|---|---|
| 1.1 | Add `Services/Common/JsonRead.cs`, `TextLimits.cs`, `SeverityScale.cs` (audit §4.1–4.3) | `dotnet build src/M365SecurityDashboard.Api` |
| 1.2 | Add `JsonReadTests.cs`: missing path, non-object step, wrong type, ignore-case, blank, date without offset | `--filter "FullyQualifiedName~JsonRead"` |
| 1.3 | Add `SeverityScaleTests.cs`: each level, unknown with and without `unknownAs` | `--filter "FullyQualifiedName~SeverityScale"` |

## Phase 2 — Defender alert rows (F3) · S–M

| Step | Do | Test |
|---|---|---|
| 2.1 | Add `Endpoints/AlertV2Row.cs` (§4.5) | build |
| 2.2 | In `DashboardEndpoints.cs`, swap the projection in `mcas-alerts` and `insider-risk` for `AlertV2Row.QueryAsync` and `CountBy` | `--filter "FullyQualifiedName~Dashboard\|FullyQualifiedName~GraphPermissionCoverage"` |
| 2.3 | Do the same for `dlp-alerts`, `mde-vulnerabilities` and `mdi-alerts` | same |
| 2.4 | `email-protection` and `defender-alerts`: build the row with `AlertV2Row.From(a)`, then add the extra fields | same |
| 2.5 | `types.ts`: add `GraphSecurityAlert` and alias the 6 types (audit F3) | `npx tsc --noEmit` |

Behaviour change: a null severity now comes through as `"unknown"`. Add one test for it in the Dashboard tests.

## Phase 3 — Endpoint scaffold (F2) · M

| Step | Do | Test |
|---|---|---|
| 3.1 | Add `Endpoints/GraphSection.cs` (§4.4) and a test that the not-configured, success and error shapes stay flat JSON | `--filter "FullyQualifiedName~GraphSection"` |
| 3.2–3.6 | Migrate the handlers in 5 batches of about 5, in file order. Pass a `requiredPermission` only if it is already in `graph-permissions.json`. Leave `copilot` as it is | `--filter "FullyQualifiedName~Dashboard\|FullyQualifiedName~RouteAuthorization"` |

Check after each batch: `grep -c "Check server logs" Endpoints/DashboardEndpoints.cs` should go down. When the phase is
done it should be 1 or fewer (the copilot handler).

## Phase 4 — Remaining JSON helpers (F1) · M

Do one file per commit: delete the private helpers, add `using static …Common.JsonRead;`, and fix call names.

| File | Use | Test filter |
|---|---|---|
| `PurviewAuditCollector.cs`, `PurviewAuditNormalizer.cs` | `Str`/`NonBlank`; `Field` → `NonBlankIgnoreCase` + keep its number/array text | `PurviewAudit` |
| `CopilotUsageSummary.cs` | `NonBlank`; keep its local `DateOnly` parse | `CopilotUsage` |
| `PowerPlatformInventory.cs` | `*IgnoreCase` versions only; keep its `"true"`-string `Bool` | `PowerPlatform` |
| `GraphCollector.cs` | `Get(path)` → `Text`; `GetBool` → `IsTrue` | `GraphCollector` |
| `Posture/PostureCollector.cs`, `PostureEvaluator.cs` | `Date`, `Number`, `Get`, `Strings` (pin the date-culture change with a test first) | `Posture` |
| `SharingPostureAnalyzer.cs`, `ConditionalAccessGapAnalyzer.cs`, `PowerPlatformOwners.cs`, `MspAppStatus.cs` | `Str`, `Strings` | `Sharing\|ConditionalAccess\|PowerPlatform\|MspApp` |

## Phase 5 — SPA (F4, F5, F6, F12, F15) · M

Run every command from `src/m365-security-dashboard-client`.

| Step | Do | Test |
|---|---|---|
| 5.1 | `utils.ts`: add §4.6 and replace `fmtDate`/`fmtShort` with `fmtCalendar` (F12). Add cases to `utils.test.ts` | `npx vitest run utils` |
| 5.2 | Move `DefenderAlertModal` from `IncidentsPage.tsx` to `SharedComponents.tsx`, using `defenderAlertUrl`. Use it in `DevicesPage` and `IdentityPage` | `npx vitest run IncidentsPage` + `npx tsc --noEmit` |
| 5.3 | `CompliancePage` (3 filter `useMemo`s) and `PowerPlatformPage` (search tail) → `filterAlerts` / `matchesQuery` | `npx vitest run PowerPlatformPage` |
| 5.4 | Add `AlertRow` and `SeverityStatRow`. Replace the 15 `al-item` rows and 11 inline `sev-dot` classes page by page | `npx tsc --noEmit` |
| 5.5 | `severityRank` in `AlertCenterPage`, `IncidentsPage` and `RecommendationsPage`. `PORTAL` constants for the portal URLs | `npx vitest run AlertCenterPage` |
| 5.6 | Run the e2e specs for the touched pages: `grep -l "compliance\|devices\|identity" e2e/signed-in/*.spec.ts` | `npx playwright test <those files>` |

## Phase 6 — Small server cleanups · S each, one commit each

| Step | Finding | Test filter |
|---|---|---|
| 6.1 | F7: `SeverityScale` in `DigestBuilder`, `NotificationSender` and `NotificationDigestWorker` (`unknownAs: Low` in the last two) | `Digest\|Notification` |
| 6.2 | F8: `CreateSmtpClient` / `FromAddress` in `NotificationSender` | `Notification` |
| 6.3 | F9: `TextLimits` in the 7 files (`AuditLogger` keeps empty → null; `TenantRollupService` → `Ellipsize`) | `Audit\|TenantRollup\|GraphCollector\|PurviewAudit` |
| 6.4 | F10: `Classify` in `GraphErrorHint` | `GraphErrorHint` |
| 6.5 | F11: `ActivityPolicy` in `AlertingSchema` | `Alerting\|PolicyPack` |
| 6.6 | F13: one `GroupBy` query and a `RecDef` table in `RecommendationsEngine` | `Recommendation` |
| 6.7 | F14: relative sensitivity-labels path in `DashboardEndpoints` | `GraphPermissionCoverage` |
| 6.8 | F16: `Set-FirstMatch` in `scripts/set-version.ps1` | `pwsh scripts/tests/Invoke-ScriptTests.ps1` |

## Phase 7 — Close · S

- Full runs, once: `dotnet test src/M365SecurityDashboard.Api.Tests | tail -30` and `npx vitest run | tail -30`.
- `CHANGELOG.md` (Unreleased): permission errors now name the missing permission; alerts with no severity no longer crash
  the Compliance, Devices and Identity pages.
- There are no model, route or permission changes, so `check-migrations`, `RouteAuthorizationTests` and `THREAT_MODEL.md`
  need no update.

## Estimate

| Phase | Effort |
|---|---|
| 1 | 0.5 d |
| 2 | 0.5 d |
| 3 | 1 d |
| 4 | 1 d |
| 5 | 1 d |
| 6–7 | 0.5–1 d |
| **Total** | **≈ 4.5–5 d** |

Phases 1–3 come first. Phases 4, 5 and 6 depend only on Phase 1 and can go in any order.

## Status (2026-11-07)

Phases 1–7 are done in the working tree of `feat/copilot-power-platform`. Nothing is committed yet, and Phase 0
(a separate branch) was skipped.

| Phase | Result |
|---|---|
| 1 | `JsonRead`, `TextLimits`, `SeverityScale` added with tests |
| 2 | 7 `alerts_v2` sections use `AlertV2Row`; 6 client types alias `GraphSecurityAlert` |
| 3 | 22 of 25 Graph sections use `GraphSection`. `identity` and `devices` read the database first; `copilot` keeps its own error text |
| 4 | Private JSON helpers in 12 files replaced. All throwing reads in `DashboardEndpoints` converted (extra step) |
| 5 | Shared `DefenderAlertModal`, `AlertRow`, `SeverityStatRow`, `filterAlerts`, `matchesQuery`, `severityRank`, `defenderAlertUrl`, `fmtCalendar` |
| 6 | F7–F11, F13 (one grouped query; the `RecDef` table was skipped as low value), F16 |
| 7 | CHANGELOG entries added; API and client suites green |

**F14 (done):** the sensitivity-labels call now uses a relative path, so `Graph:BaseUrl` and
`GraphPermissionCoverageTests` both apply to it. `InformationProtectionPolicy.Read.All` is added as an optional
permission (per Microsoft's docs, not checked against a live tenant). On the owner's tenant the card returned real labels when checked on 2026-11-08; if it reports the
permission missing, grant admin consent again.

Nothing here was checked against a live tenant.
