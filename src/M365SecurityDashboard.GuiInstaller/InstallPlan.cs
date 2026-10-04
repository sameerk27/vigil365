using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace M365SecurityDashboard.GuiInstaller
{
    /// <summary>Which edition the install runs as (written as Edition:Mode).</summary>
    public enum EditionChoice { Single, Msp }

    /// <summary>Which database engine the install uses (written as Database:Provider).</summary>
    public enum DbEngine { SqlServer, Postgres }

    /// <summary>The step an install run was on when it failed; decides what the failure screen advises.</summary>
    public enum InstallStage { SignIn, DatabaseChoice, SqlServer, Postgres, AppRegistration, Files, Service }

    /// <summary>The rights an installer ACL grants (mapped to FileSystemRights by MainWindow).</summary>
    public enum AclRights { Read, Modify, FullControl }

    /// <summary>One allow entry: a well-known SID (locale-independent) and what it may do.</summary>
    public sealed record AclGrant(string Sid, AclRights Rights);

    /// <summary>
    /// An ACL that replaces the target's: with <see cref="ProtectFromParent"/> nothing is
    /// inherited from the folder above (Program Files and ProgramData let every local
    /// user read), so only <see cref="Grants"/> apply. <see cref="InheritedByChildren"/>
    /// carries them to everything below a folder.
    /// </summary>
    public sealed record RestrictedAcl(IReadOnlyList<AclGrant> Grants, bool ProtectFromParent, bool InheritedByChildren);

    /// <summary>A Windows service's state, as "sc query" reports it.</summary>
    public enum ServiceState { NotInstalled, Stopped, Running, Changing }

    /// <summary>
    /// The installer's decisions, kept free of WPF so they can be unit tested
    /// (this file is linked into the API test project). MainWindow gathers the
    /// operator's choices, asks this class what they mean, and does the I/O.
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
        ///
        /// A PATCH replaces a redirect list whole, so the app's existing redirect
        /// URIs are passed in and kept: replacing them with this server's URL broke
        /// sign-in for any other install sharing the registration (AADSTS50011).
        /// For the same reason the app's current audience is passed in and never
        /// narrowed (see <see cref="SignInAudience"/>).
        /// </summary>
        public static string AppPatchJson(EditionChoice edition, string publicUrl, string requiredResourceAccessJson, string apiBlock,
                                          IReadOnlyList<string>? existingSpaRedirects = null, IReadOnlyList<string>? existingWebRedirects = null,
                                          string? currentAudience = null)
        {
            var audience = SignInAudience(edition, currentAudience);
            var web = edition == EditionChoice.Msp
                ? $"\n    \"web\": {{ \"redirectUris\": {JsonSerializer.Serialize(WithUri(existingWebRedirects, ConsentRedirect(publicUrl)))} }},"
                : "";
            return $$"""
            {
                "signInAudience": "{{audience}}",{{web}}
                "spa": { "redirectUris": {{JsonSerializer.Serialize(WithUri(existingSpaRedirects, publicUrl))}} },
                "requiredResourceAccess": {{requiredResourceAccessJson}}{{apiBlock}}
            }
            """;
        }

        /// <summary>
        /// The signInAudience to write. MSP mode needs a multi-tenant app; Single
        /// mode asks for a single-tenant one, but never narrows an app that is
        /// already multi-tenant: a reused registration may be an MSP install's, and
        /// AzureADMyOrg would make every consented client's token request fail
        /// (AADSTS700016). Dashboard sign-in stays pinned to the install's own
        /// tenant either way.
        /// </summary>
        public static string SignInAudience(EditionChoice edition, string? currentAudience)
            => edition == EditionChoice.Msp || string.Equals(currentAudience, "AzureADMultipleOrgs", StringComparison.OrdinalIgnoreCase)
                ? "AzureADMultipleOrgs"
                : "AzureADMyOrg";

        /// <summary>
        /// The name of a registration a server creates for itself when the tenant
        /// already holds a "Vigil365" that its configuration does not name — most
        /// likely another server's, which it must not take over silently.
        /// </summary>
        public static string HostAppName(string machineName) => $"Vigil365 ({machineName})";

        private static List<string> WithUri(IReadOnlyList<string>? existing, string uri)
        {
            var list = existing?.ToList() ?? new List<string>();
            if (!list.Contains(uri, StringComparer.OrdinalIgnoreCase)) list.Add(uri);
            return list;
        }

        /// <summary>
        /// The redirect URIs of one platform ("spa" or "web") from an app
        /// registration (az ad app show -o json). Empty when there are none or the
        /// JSON is not an app.
        /// </summary>
        public static IReadOnlyList<string> RedirectUris(string appJson, string platform)
        {
            try
            {
                var app = JsonSerializer.Deserialize<JsonElement>(appJson);
                if (app.ValueKind == JsonValueKind.Object
                    && app.TryGetProperty(platform, out var p) && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("redirectUris", out var uris) && uris.ValueKind == JsonValueKind.Array)
                    return uris.EnumerateArray().Select(u => u.GetString()).OfType<string>().ToList();
            }
            catch (JsonException) { }
            return Array.Empty<string>();
        }

        /// <summary>
        /// The appIds of the registrations named exactly <paramref name="displayName"/>,
        /// from az ad app list --display-name (which matches names that merely START
        /// with it — "Vigil365 (Pilot)" too). Empty when the output is not a list.
        /// </summary>
        public static IReadOnlyList<string> AppIdsNamedExactly(string appListJson, string displayName)
        {
            try
            {
                var apps = JsonSerializer.Deserialize<JsonElement>(appListJson);
                if (apps.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
                return apps.EnumerateArray()
                    .Where(a => a.TryGetProperty("displayName", out var n) && n.GetString() == displayName
                                && a.TryGetProperty("appId", out _))
                    .Select(a => a.GetProperty("appId").GetString())
                    .OfType<string>()
                    .ToList();
            }
            catch (JsonException) { return Array.Empty<string>(); }
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

        /// <summary>
        /// What an existing install was configured as, to preselect on re-run.
        /// ClientId is the app registration it signs in with — the one a re-run
        /// must reuse — or null when the config names none (or a placeholder).
        /// </summary>
        public sealed record ExistingInstall(EditionChoice Edition, DbEngine Engine, string? ConnectionString, string? ClientId = null);

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
                var clientId = Guid.TryParse(Get("AzureAd", "ClientId"), out var id) ? id.ToString() : null;
                return new ExistingInstall(edition, engine, Get("ConnectionStrings", "DefaultConnection"), clientId);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// The appsettings.Production.json to write: the previous file with the
        /// installer's own values laid over it. Re-running Setup is how a
        /// certificate is replaced or an install converted to MSP, and it used to
        /// rewrite the file from scratch — silently dropping settings it does not
        /// manage (Retention, Graph tuning, Database:SizeWarningBytes). Objects are
        /// merged key by key (case-insensitively, as .NET configuration reads
        /// them); Kestrel is replaced whole, because a previous certificate node
        /// left beside a new one is a config Kestrel refuses. An unreadable
        /// previous file is ignored.
        /// </summary>
        public static string MergeConfig(string? previousJson, string installerJson)
        {
            var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var nodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
            var installer = JsonNode.Parse(installerJson, nodeOptions, docOptions)!.AsObject();

            JsonObject? merged = null;
            if (!string.IsNullOrWhiteSpace(previousJson))
            {
                try { merged = JsonNode.Parse(previousJson, nodeOptions, docOptions) as JsonObject; }
                catch (JsonException) { }
            }
            if (merged == null) merged = installer;
            else Overlay(merged, installer, root: true);

            return merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        private static void Overlay(JsonObject target, JsonObject values, bool root)
        {
            foreach (var (key, value) in values)
            {
                if (value is JsonObject inner && target[key] is JsonObject existing
                    && !(root && key.Equals("Kestrel", StringComparison.OrdinalIgnoreCase)))
                    Overlay(existing, inner, root: false);
                else
                    target[key] = value?.DeepClone();
            }
        }

        /// <summary>
        /// The failure screen's title and advice for the step that failed. Chosen by
        /// step, not by searching the message: "az login" contains "login", a
        /// service that dies on start names SQL Server among its possible causes,
        /// and the MSP Express refusal names SQL Server too — all of which used to
        /// get the database advice, including "nothing has been installed as a
        /// service yet" after the service had been.
        /// </summary>
        public static (string Title, string Remedy) FailureAdvice(InstallStage stage) => stage switch
        {
            InstallStage.SignIn => (
                "Vigil365 could not sign in to your Microsoft 365 tenant.",
                "• Check the administrator email and tenant: use the domain your users sign in with, or the tenant's .onmicrosoft.com name.\n" +
                "• When the browser opens, sign in with an account in that tenant. If the Azure CLI is signed in as someone else, run  az logout  first.\n" +
                "• Nothing has been changed on this computer or in Entra yet, so it is safe to fix this and try again."),
            InstallStage.DatabaseChoice => (
                "This database cannot be used in MSP mode.",
                "• Go back and enter a connection string for SQL Server Standard, Enterprise or Azure SQL, or choose PostgreSQL.\n" +
                "• Or install in Single-organisation mode, which runs on SQL Server Express.\n" +
                "• Nothing has been changed on this computer yet, so it is safe to fix this and try again."),
            InstallStage.SqlServer => (
                "Vigil365 could not set up its database.",
                "• Check SQL Server is running: open Services and look for the SQL Server instance the connection string names.\n" +
                "• Go back and check the connection string names a server you can reach from this computer.\n" +
                "• You must be a SQL administrator on that instance. If someone else administers SQL Server, " +
                "ask them to run this installer, or to create a login for NT AUTHORITY\\LOCAL SERVICE with " +
                "db_owner on the Vigil365 database.\n" +
                "• The Vigil365 service and its files have not been changed yet, so it is safe to fix this and try again."),
            InstallStage.Postgres => (
                "Vigil365 could not set up its PostgreSQL database.",
                "• Check the PostgreSQL server is running and reachable from this computer (host, port, firewall, pg_hba.conf).\n" +
                "• Go back and check the connection string's Host, Database, Username and Password.\n" +
                "• That user must be able to create the database, or it must already exist and be owned by that user. " +
                "Vigil365 does not install PostgreSQL.\n" +
                "• The Vigil365 service and its files have not been changed yet, so it is safe to fix this and try again."),
            InstallStage.AppRegistration => (
                "Vigil365 could not register itself with Microsoft Entra.",
                "• Your account needs permission to create and update app registrations (Application Administrator or " +
                "Cloud Application Administrator). If it does not have that, ask an administrator to run this installer.\n" +
                "• If the log says several app registrations are named Vigil365, rename or delete the ones this server should not use.\n" +
                "• The Vigil365 service and its files have not been changed yet, so it is safe to fix this and try again."),
            InstallStage.Files => (
                "Vigil365 could not install its application files.",
                "• If the log says the application payload is missing, this build is incomplete: download the official " +
                "Vigil365-Setup.exe, or rebuild it with scripts/build-installer.ps1.\n" +
                "• Otherwise a file in the install folder is locked: the previous Vigil365 service did not stop, or another " +
                "program (antivirus, an open log viewer) holds it. Close it and try again.\n" +
                "• If Vigil365 was already running here, the installer started it again — check it in Services."),
            _ => (
                "Vigil365 could not set up or start its Windows service.",
                "• Windows Event Viewer > Windows Logs > Application records why the service stopped.\n" +
                "• Common causes: the port is already in use, the service cannot read its certificate, or it cannot reach its database.\n" +
                "• The app registration, files and configuration are in place. Fix the cause and run the installer again; it reuses them."),
        };

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

        // ── Who may read what the installer writes ──────────────────────────────

        /// <summary>BUILTIN\Administrators.</summary>
        public const string AdministratorsSid = "S-1-5-32-544";
        /// <summary>NT AUTHORITY\SYSTEM.</summary>
        public const string LocalSystemSid = "S-1-5-18";
        /// <summary>NT AUTHORITY\LOCAL SERVICE, the account the Vigil365 service runs as.</summary>
        public const string LocalServiceSid = "S-1-5-19";

        /// <summary>
        /// appsettings.Production.json and its .bak. They hold the Graph client secret
        /// (in MSP mode, the key to every consenting client's data) and the certificate
        /// password, so the service may read them and only administrators and SYSTEM
        /// may do more. Nobody else — Program Files lets every local user read.
        /// </summary>
        public static RestrictedAcl ConfigFileAcl() => Restricted(AclRights.Read, inheritedByChildren: false);

        /// <summary>
        /// %ProgramData%\Vigil365 and everything below it: the data-protection key ring,
        /// which decrypts every secret Vigil365 keeps in its database, and the logs, which
        /// name users and devices. The service writes both; no one else but
        /// administrators and SYSTEM may read them — ProgramData lets every local user read.
        /// </summary>
        public static RestrictedAcl DataFolderAcl() => Restricted(AclRights.Modify, inheritedByChildren: true);

        // The service account never gets FullControl: it must not be able to rewrite
        // the ACL that keeps everyone else out.
        private static RestrictedAcl Restricted(AclRights service, bool inheritedByChildren) => new(new[]
        {
            new AclGrant(AdministratorsSid, AclRights.FullControl),
            new AclGrant(LocalSystemSid, AclRights.FullControl),
            new AclGrant(LocalServiceSid, service),
        }, ProtectFromParent: true, InheritedByChildren: inheritedByChildren);

        // ── Replacing the files of a running service ────────────────────────────

        /// <summary>
        /// The state in "sc query" output ("STATE : 4  RUNNING"), read from the state
        /// name rather than the "STATE" label, so a translated label does not read as
        /// "not installed". No state at all means the service is not installed (sc
        /// prints error 1060 instead).
        /// </summary>
        public static ServiceState ParseServiceState(string? scQueryOutput)
        {
            var m = System.Text.RegularExpressions.Regex.Match(scQueryOutput ?? "",
                @":\s*\d+\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b");
            if (!m.Success) return ServiceState.NotInstalled;
            return m.Groups[1].Value switch
            {
                "STOPPED" => ServiceState.Stopped,
                "RUNNING" => ServiceState.Running,
                _ => ServiceState.Changing,
            };
        }

        /// <summary>
        /// The application files can be overwritten. "sc stop" only asks: the service
        /// then finishes its collection cycle and drains its hosted services while it
        /// still holds its files, so anything but stopped (or absent) means wait.
        /// </summary>
        public static bool FilesReplaceable(ServiceState state) => state is ServiceState.Stopped or ServiceState.NotInstalled;

        /// <summary>
        /// Whether to start the previous service again when replacing its files failed
        /// (it did not stop in time, or extraction failed). A service stopped cleanly
        /// is not restarted by its recovery actions, so a failed upgrade left monitoring
        /// down until someone noticed. One that was not running stays as it was.
        /// </summary>
        public static bool RestartAfterFailedUpgrade(ServiceState before) => before == ServiceState.Running;

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
