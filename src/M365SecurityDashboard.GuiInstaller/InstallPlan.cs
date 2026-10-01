using System;
using System.Collections.Generic;
using System.Text.Json;

namespace M365SecurityDashboard.GuiInstaller
{
    /// <summary>Which edition the install runs as (written as Edition:Mode).</summary>
    public enum EditionChoice { Single, Msp }

    /// <summary>Which database engine the install uses (written as Database:Provider).</summary>
    public enum DbEngine { SqlServer, Postgres }

    /// <summary>
    /// The installer's MSP-mode decisions, kept free of WPF so they can be unit
    /// tested (this file is linked into the API test project). MainWindow gathers
    /// the operator's choices, asks this class what they mean, and does the I/O.
    /// See docs/MSP_V12_PLAN.md items M3/M4.
    /// </summary>
    public static class InstallPlan
    {
        /// <summary>SERVERPROPERTY('EngineEdition') value for SQL Server Express.</summary>
        public const int SqlExpressEngineEdition = 4;

        /// <summary>
        /// Null when the database choice is acceptable for the edition, otherwise a
        /// message to show the operator. MSP mode refuses SQL Server Express: its
        /// 10 GB database and ~1.4 GB memory ceilings are reached within a few dozen
        /// client tenants, and writes then fail outright.
        /// </summary>
        public static string? DatabaseProblem(EditionChoice edition, DbEngine engine, bool installExpress, int? sqlEngineEdition)
        {
            if (edition != EditionChoice.Msp || engine == DbEngine.Postgres) return null;
            if (installExpress || sqlEngineEdition == SqlExpressEngineEdition)
                return "MSP mode needs SQL Server Standard, Enterprise or Azure SQL, or PostgreSQL. " +
                       "SQL Server Express stops accepting writes at 10 GB and is limited to about 1.4 GB of memory — " +
                       "an MSP install reaches that within a few dozen client tenants.\r\n\r\n" +
                       "Point Vigil365 at a full SQL Server or a PostgreSQL server, or install in Single-organisation mode. " +
                       "To convert an existing Express install, back it up and restore it to a full edition first.";
            return null;
        }

        /// <summary>The Web redirect Microsoft sends a client's admin back to after consent.</summary>
        public static string ConsentRedirect(string publicUrl) => publicUrl.TrimEnd('/') + "/consented";

        /// <summary>
        /// PATCH body for the app registration. MSP mode makes it multi-tenant (so
        /// client tenants can consent to its Graph permissions) and registers the
        /// /consented Web redirect; Single mode keeps it single-tenant. Sign-in to
        /// the dashboard stays pinned to the install's own tenant either way
        /// (Edition:Mode + SignInTenantPin in the API). Re-running the installer
        /// over a Single install in MSP mode reuses and patches the same app —
        /// that is the "convert to MSP" path: no new app, no new secret.
        /// </summary>
        public static string AppPatchJson(EditionChoice edition, string publicUrl, string requiredResourceAccessJson, string apiBlock)
        {
            var audience = edition == EditionChoice.Msp ? "AzureADMultipleOrgs" : "AzureADMyOrg";
            var web = edition == EditionChoice.Msp
                ? $"\n    \"web\": {{ \"redirectUris\": [ {JsonSerializer.Serialize(ConsentRedirect(publicUrl))} ] }},"
                : "";
            return $$"""
            {
                "signInAudience": "{{audience}}",{{web}}
                "spa": { "redirectUris": [ {{JsonSerializer.Serialize(publicUrl)}} ] },
                "requiredResourceAccess": {{requiredResourceAccessJson}}{{apiBlock}}
            }
            """;
        }

        /// <summary>The Edition and Database sections for appsettings.Production.json (ends with a comma).</summary>
        public static string ConfigSections(EditionChoice edition, DbEngine engine)
            => $$"""
                "Edition": {
                    "Mode": "{{(edition == EditionChoice.Msp ? "Msp" : "Single")}}"
                },
                "Database": {
                    "Provider": "{{(engine == DbEngine.Postgres ? "Postgres" : "SqlServer")}}"
                },
            """;

        /// <summary>What an existing install was configured as, to preselect on re-run.</summary>
        public sealed record ExistingInstall(EditionChoice Edition, DbEngine Engine, string? ConnectionString);

        /// <summary>
        /// Reads an existing appsettings.Production.json. Missing sections mean the
        /// pre-v1.2 defaults (Single, SQL Server). Null if the file is unreadable —
        /// the wizard then just shows its own defaults.
        /// </summary>
        public static ExistingInstall? ReadExisting(string? appsettingsJson)
        {
            if (string.IsNullOrWhiteSpace(appsettingsJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(appsettingsJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var root = doc.RootElement;
                string? Get(string section, string key) =>
                    root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() : null;

                var edition = string.Equals(Get("Edition", "Mode"), "Msp", StringComparison.OrdinalIgnoreCase) ? EditionChoice.Msp : EditionChoice.Single;
                var provider = Get("Database", "Provider");
                var engine = provider is not null && (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) || provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
                    ? DbEngine.Postgres : DbEngine.SqlServer;
                return new ExistingInstall(edition, engine, Get("ConnectionStrings", "DefaultConnection"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// The app-role id for a Graph permission, from the Graph service principal's
        /// appRoles JSON (az ad sp show ... --query appRoles). Used to grant
        /// Application.Read.All to the MSP's own app in the MSP tenant only — a
        /// direct role assignment, deliberately not in requiredResourceAccess, so
        /// clients never consent to it. It powers the MSP app readiness card.
        /// </summary>
        public static string? AppRoleId(string appRolesJson, string permission)
        {
            foreach (var role in JsonSerializer.Deserialize<JsonElement>(appRolesJson).EnumerateArray())
                if (role.TryGetProperty("value", out var v) && string.Equals(v.GetString(), permission, StringComparison.OrdinalIgnoreCase)
                    && role.TryGetProperty("id", out var id))
                    return id.GetString();
            return null;
        }

        /// <summary>Next steps shown on the completion page.</summary>
        public static IReadOnlyList<string> NextSteps(EditionChoice edition) => edition == EditionChoice.Msp
            ? new[]
            {
                "Open Vigil365 and sign in as the administrator.",
                "Go to Clients → Add client.",
                "Click \"Sign in as global admin & consent\" and have the client's Global Administrator approve.",
                "Vigil365 tests the connection and starts collecting on the next cycle.",
            }
            : new[]
            {
                "Open Vigil365 and sign in as the administrator.",
                "Collection starts on its own; the first results appear within one collection cycle.",
            };
    }
}
