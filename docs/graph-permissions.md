# Graph Permission Reference

Vigil365 uses Microsoft Graph **application** permissions (unattended collection) and
only reads. Every permission is read-only except the optional
`AttackSimulation.ReadWrite.All` (below). This table is generated from `graph-permissions.json` at the repo root —
the single list the installer, the API and `register-app.ps1` all use. A test fails if
this page and that file disagree.

## Required

| Feature | Permission |
| --- | --- |
| Defender XDR alerts | `SecurityAlert.Read.All` |
| Defender XDR incidents | `SecurityIncident.Read.All` |
| Entra ID risky users | `IdentityRiskyUser.Read.All` |
| Risk detections | `IdentityRiskEvent.Read.All` |
| Sign-in and directory audit logs | `AuditLog.Read.All` |
| MFA registration and authentication-method reports; Copilot usage | `Reports.Read.All` |
| Intune managed devices | `DeviceManagementManagedDevices.Read.All` |
| Microsoft 365 service health | `ServiceHealth.Read.All` |
| Conditional Access policies | `Policy.Read.All` |
| Users, groups and directory roles | `Directory.Read.All` |
| PIM role assignments | `PrivilegedAccess.Read.AzureAD` |
| Advanced hunting (Defender for Identity, Cloud Apps) | `ThreatHunting.Read.All` |
| MFA method detail | `UserAuthenticationMethod.Read.All` |
| SharePoint sharing posture | `SharePointTenantSettings.Read.All` |
| Secure Score and quarantined messages | `SecurityEvents.Read.All` |

## Optional

Requested with the required ones, so admin consent grants them too. Vigil365 works
without them. `AttackSimulation.ReadWrite.All` also allows creating and launching
phishing simulations; Vigil365 only reads results with it.

`AuditLogsQuery.Read.All` is read-only. It is the reason for the one POST Vigil365
sends to Graph: an audit search is started with `POST /security/auditLog/queries`,
which creates a search job and changes nothing in the tenant. The Graph client
refuses a POST to any other path, and a test fails if code anywhere else sends one.
Without this permission the Copilot agent and Power Platform rules stay idle and
Rules & Notifications says so; nothing else is affected.

| Feature | Permission |
| --- | --- |
| Attack simulation results (Graph has no read-only variant; a tenant may refuse it) | `AttackSimulation.ReadWrite.All` |
| Defender for Identity sensor health | `SecurityIdentitiesHealth.Read.All` |
| Copilot agent, Power Automate and Power Apps alerts (Purview audit search) | `AuditLogsQuery.Read.All` |
| Intune compliance policies (licence posture) | `DeviceManagementConfiguration.Read.All` |

Without `DeviceManagementConfiguration.Read.All` the licence posture check for an Intune
compliance policy says "Unknown" with the reason; the other checks are unaffected.

## Not a Graph permission: Power Platform reader

The optional Power Platform inventory reads the Power Platform API, which Graph consent
doesn't cover. A tenant turns it on by giving Vigil365's app Microsoft's read-only
"Power Platform reader" role (preview), with `grant-power-platform-reader.ps1` at the
repository root; `-Remove` takes it away. See `docs/COPILOT_POWER_PLATFORM_PLAN.md`, phase D.

Admin consent is required. In the MSP edition each client's Global Administrator grants it
once, through the onboarding consent popup. Changing this list after clients have consented
means every client must consent again.

**MSP only:** the *MSP app status* card reads your own app registration and needs
`Application.Read.All` granted in **your own tenant** only. It is deliberately not in the list
above, so clients never consent to it.
