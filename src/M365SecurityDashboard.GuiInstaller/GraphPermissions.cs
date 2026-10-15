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
        /// Application (not delegated) permissions, from the embedded
        /// graph-permissions.json at the repo root — the same file the API embeds
        /// and register-app.ps1 reads, so they can no longer drift apart. Kept as
        /// names and resolved against the tenant's own Graph service principal,
        /// because a wrong hard-coded GUID fails as an opaque "invalid value".
        /// </summary>
        public static readonly string[] Required = Load("required");

        /// <summary>
        /// Permissions that are genuinely optional — the feature degrades to a
        /// permission-error card rather than the install being broken.
        /// </summary>
        public static readonly string[] Optional = Load("optional");

        private static string[] Load(string key)
        {
            using var stream = typeof(GraphPermissions).Assembly.GetManifestResourceStream("Vigil365.graph-permissions.json")
                ?? throw new InvalidOperationException("graph-permissions.json is not embedded in the installer.");
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.GetProperty(key).EnumerateArray()
                .Select(p => p.GetProperty("name").GetString()!)
                .ToArray();
        }

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
