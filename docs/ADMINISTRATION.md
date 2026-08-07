# Administration & Configuration

Vigil365 provides built-in tools for managing who has access to the dashboard and configuring how the system behaves. All administrative actions are recorded in a tamper-evident audit log.

## User Management & RBAC

Access to Vigil365 is controlled via in-app Role-Based Access Control (RBAC). When the application is first installed via the setup wizard, the user who runs the setup is automatically granted the **Admin** role.

From the **Administration > User Management** page, Admins can invite other users from your Microsoft 365 tenant.

### Available Roles

| Role | Permissions |
|------|-------------|
| **Admin** | Full access. Can invite/remove users, change application configuration, configure notification channels, and modify alert policies. |
| **Analyst** | Triage access. Can acknowledge, resolve, and snooze alerts. Can view all reports, dashboards, and investigations, but cannot change system configuration or invite users. |
| **Viewer** | Read-only access. Can view all dashboards, alerts, and reports, but cannot modify alert states or configurations. |

### Tamper-Evident Audit Trail
To ensure accountability, every privileged action taken within Vigil365 (e.g., inviting a user, changing a role, modifying a policy) is permanently recorded in the **Audit Log**. This log is SHA-256 hash-chained, meaning that any attempt to manually tamper with or delete records in the underlying SQL database will be detected and flagged by the application.

---

## Initial Setup & Configuration

When you launch Vigil365 for the first time, you will be guided through a setup checklist to ensure the dashboard can successfully collect data from your tenant.

### 1. Microsoft Graph Connection
Vigil365 requires a connection to your Microsoft 365 tenant to aggregate security alerts. 
If you used the Interactive Setup Wizard, this Entra ID App Registration was created automatically. 
If the dashboard reports missing permissions, navigate to **Administration > Setup** to view exactly which Graph API permissions are missing and grant Admin Consent in the Azure Portal.

### 2. Notification Channels (SMTP & Webhooks)
To receive alerts outside of the dashboard, you must configure your notification channels:
- **Email (SMTP):** Configure your SMTP server details to enable Daily/Weekly Executive Digest reports and email alerts.
- **Teams / Slack / Generic Webhooks:** You can route specific alert policies to external chat channels or SIEMs.

All secrets (like SMTP passwords and webhook URLs) are encrypted at rest using the Windows Data Protection API (DPAPI) and are never exposed in plaintext to the frontend.

### 3. Alert Policies
By default, Vigil365 imports a set of best-practice alert policies. You can customize these thresholds or create entirely new anomaly/activity-based policies in the **Alert Center > Policies** tab. 

*Tip: Before enabling a new policy, use the "Dry Run" feature to backtest it against your historical data to see how many times it would have fired in the past 30 days.*
