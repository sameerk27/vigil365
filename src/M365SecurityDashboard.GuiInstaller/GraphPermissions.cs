using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace M365SecurityDashboard.GuiInstaller
{
    /// <summary>
    /// The Microsoft Graph application permissions Vigil365 needs in order to
    /// collect anything.
    ///
    /// The installer used to register only the sign-in application, so a new
    /// install could authenticate people and then show them nothing: every
    /// collector failed on authorization until an administrator went to the
    /// portal and added these fifteen permissions by hand. The README pointed at
    /// a register-app.ps1 to do it, and that script does not exist.
    /// </summary>
    internal static class GraphPermissions
    {
        /// <summary>The Microsoft Graph service principal, the same id in every tenant.</summary>
        public const string GraphAppId = "00000003-0000-0000-c000-000000000000";

        /// <summary>
        /// Application (not delegated) permissions, matched to the table in the
        /// README. Kept as names rather than GUIDs and resolved against the
        /// tenant's own Graph service principal, because a wrong hard-coded GUID
        /// fails as an opaque "invalid value" with nothing naming the permission.
        /// </summary>
        public static readonly string[] Required =
        [
            "SecurityAlert.Read.All",                    // Defender XDR alerts
            "SecurityIncident.Read.All",                 // Defender XDR incidents
            "IdentityRiskyUser.Read.All",                // Entra ID risky users
            "IdentityRiskEvent.Read.All",                // Risk detections
            "AuditLog.Read.All",                         // Sign-in and audit logs
            "Reports.Read.All",                          // MFA registration, auth methods
            "DeviceManagementManagedDevices.Read.All",   // Intune devices
            "ServiceHealth.Read.All",                    // M365 service health
            "Policy.Read.All",                           // Conditional Access policies
            "Directory.Read.All",                        // Users, groups, PIM
            "PrivilegedAccess.Read.AzureAD",             // PIM assignments
            "ThreatHunting.Read.All",                    // Advanced hunting / MDI
            "UserAuthenticationMethod.Read.All",         // MFA method detail
            "SharePointTenantSettings.Read.All",         // Sharing posture
        ];

        /// <summary>
        /// Permissions that are genuinely optional — the feature degrades to a
        /// permission-error card rather than the install being broken. Graph has
        /// no read-only variant of the attack-simulation permission, so a tenant
        /// may reasonably refuse it.
        /// </summary>
        public static readonly string[] Optional =
        [
            "AttackSimulation.ReadWrite.All",
        ];

        /// <summary>
        /// Maps permission names to the role ids this tenant uses, from the Graph
        /// service principal's own appRoles. Names not offered by the tenant are
        /// reported rather than silently dropped.
        /// </summary>
        public static (string Json, List<string> Missing) BuildRequiredResourceAccess(
            string appRolesJson, IEnumerable<string> wanted)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in JsonSerializer.Deserialize<JsonElement>(appRolesJson).EnumerateArray())
            {
                var value = role.TryGetProperty("value", out var v) ? v.GetString() : null;
                var id = role.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(id)) byName[value] = id;
            }

            var missing = new List<string>();
            var entries = new List<string>();
            foreach (var name in wanted)
            {
                if (byName.TryGetValue(name, out var id))
                    entries.Add($$"""{"id":"{{id}}","type":"Role"}""");
                else
                    missing.Add(name);
            }

            var sb = new StringBuilder();
            sb.Append($$"""[{"resourceAppId":"{{GraphAppId}}","resourceAccess":[""");
            sb.Append(string.Join(",", entries));
            sb.Append("]}]");
            return (sb.ToString(), missing);
        }
    }
}
