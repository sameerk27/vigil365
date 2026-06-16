# Graph Permission Reference

Use application permissions for unattended local collection.

| Feature | Permission |
| --- | --- |
| Risky users | `IdentityRiskyUser.Read.All` |
| Risky sign-ins and failed sign-ins | `AuditLog.Read.All` |
| MFA registration reports | `Reports.Read.All` |
| Intune managed devices | `DeviceManagementManagedDevices.Read.All` |
| Defender XDR incidents | `SecurityIncident.Read.All` |
| Defender XDR alerts | `SecurityAlert.Read.All` |
| Microsoft 365 service health | `ServiceHealth.Read.All` |

Admin consent is required. Some tenants also require an Entra directory role or product-specific role assignment for the application/service principal.
