# Graph Permission Reference

Vigil365 uses Microsoft Graph **application** permissions (unattended collection) and
is read-only. This table is generated from `graph-permissions.json` at the repo root —
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
| MFA registration and authentication-method reports | `Reports.Read.All` |
| Intune managed devices | `DeviceManagementManagedDevices.Read.All` |
| Microsoft 365 service health | `ServiceHealth.Read.All` |
| Conditional Access policies | `Policy.Read.All` |
| Users, groups and directory roles | `Directory.Read.All` |
| PIM role assignments | `PrivilegedAccess.Read.AzureAD` |
| Advanced hunting (Defender for Identity, Cloud Apps) | `ThreatHunting.Read.All` |
| MFA method detail | `UserAuthenticationMethod.Read.All` |
| SharePoint sharing posture | `SharePointTenantSettings.Read.All` |

## Optional

| Feature | Permission |
| --- | --- |
| Attack simulation results (Graph has no read-only variant; a tenant may refuse it) | `AttackSimulation.ReadWrite.All` |

Admin consent is required. In the MSP edition each client's Global Administrator grants it
once, through the onboarding consent popup. Changing this list after clients have consented
means every client must consent again.

**MSP only:** the *MSP app status* card reads your own app registration and needs
`Application.Read.All` granted in **your own tenant** only. It is deliberately not in the list
above, so clients never consent to it.
