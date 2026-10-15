using System.Text.Json;
using System.Text.RegularExpressions;
using M365SecurityDashboard.GuiInstaller;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>MSP_V12_PLAN.md M3/M4: the installer's MSP-mode decisions.</summary>
public sealed class InstallPlanTests
{
    [Theory]
    [InlineData(EditionChoice.Single, DbEngine.SqlServer, true, null, false)]  // Single + install Express: fine
    [InlineData(EditionChoice.Single, DbEngine.SqlServer, false, 4, false)]    // Single on existing Express: fine
    [InlineData(EditionChoice.Msp, DbEngine.SqlServer, true, null, true)]      // MSP + install Express: refused
    [InlineData(EditionChoice.Msp, DbEngine.SqlServer, false, 4, true)]        // MSP on existing Express (convert too): refused
    [InlineData(EditionChoice.Msp, DbEngine.SqlServer, false, 2, false)]       // MSP on Standard: fine
    [InlineData(EditionChoice.Msp, DbEngine.SqlServer, false, 5, false)]       // MSP on Azure SQL: fine
    [InlineData(EditionChoice.Msp, DbEngine.Postgres, false, null, false)]     // MSP on Postgres: fine
    public void Msp_mode_refuses_sql_express(EditionChoice ed, DbEngine eng, bool installExpress, int? engineEdition, bool refused)
    {
        var problem = InstallPlan.DatabaseProblem(ed, eng, installExpress, engineEdition);
        Assert.Equal(refused, problem is not null);
        if (refused) Assert.Contains("Express", problem);
    }

    [Fact]
    public void Msp_patch_is_multi_tenant_with_the_consent_redirect()
    {
        var json = InstallPlan.AppPatchJson(EditionChoice.Msp, "https://vigil.msp.test", "[]", "");
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal("AzureADMultipleOrgs", doc.GetProperty("signInAudience").GetString());
        Assert.Equal("https://vigil.msp.test/consented", doc.GetProperty("web").GetProperty("redirectUris")[0].GetString());
        Assert.Equal("https://vigil.msp.test", doc.GetProperty("spa").GetProperty("redirectUris")[0].GetString());
    }

    [Fact]
    public void Single_patch_is_single_tenant_and_registers_no_consent_redirect()
    {
        var doc = JsonDocument.Parse(InstallPlan.AppPatchJson(EditionChoice.Single, "http://localhost:8080", "[]", "")).RootElement;
        Assert.Equal("AzureADMyOrg", doc.GetProperty("signInAudience").GetString());
        Assert.False(doc.TryGetProperty("web", out _));
    }

    [Fact]
    public void Patch_keeps_the_api_block_and_permissions_and_is_valid_json()
    {
        const string api = ",\n \"identifierUris\": [ \"api://abc\" ]";
        const string rra = """[{"resourceAppId":"00000003-0000-0000-c000-000000000000","resourceAccess":[{"id":"r1","type":"Role"}]}]""";
        var doc = JsonDocument.Parse(InstallPlan.AppPatchJson(EditionChoice.Msp, "https://x.test/", rra, api)).RootElement;
        Assert.Equal("api://abc", doc.GetProperty("identifierUris")[0].GetString());
        Assert.Equal("r1", doc.GetProperty("requiredResourceAccess")[0].GetProperty("resourceAccess")[0].GetProperty("id").GetString());
        Assert.Equal("https://x.test/consented", doc.GetProperty("web").GetProperty("redirectUris")[0].GetString());
    }

    [Theory]
    [InlineData(EditionChoice.Msp, DbEngine.Postgres, "Msp", "Postgres")]
    [InlineData(EditionChoice.Single, DbEngine.SqlServer, "Single", "SqlServer")]
    public void Config_sections_are_written_and_round_trip(EditionChoice ed, DbEngine eng, string mode, string provider)
    {
        var sections = InstallPlan.ConfigSections(ed, eng);
        var json = "{" + sections + "\n\"ConnectionStrings\": { \"DefaultConnection\": \"Host=db\" } }";
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal(mode, doc.GetProperty("Edition").GetProperty("Mode").GetString());
        Assert.Equal(provider, doc.GetProperty("Database").GetProperty("Provider").GetString());

        var back = InstallPlan.ReadExisting(json);
        Assert.NotNull(back);
        Assert.Equal(ed, back!.Edition);
        Assert.Equal(eng, back.Engine);
        Assert.Equal("Host=db", back.ConnectionString);
    }

    [Fact]
    public void A_pre_v12_config_reads_as_single_on_sql_server()
    {
        var back = InstallPlan.ReadExisting("""{ "ConnectionStrings": { "DefaultConnection": "Server=.\\SQLEXPRESS" } }""");
        Assert.Equal(EditionChoice.Single, back!.Edition);
        Assert.Equal(DbEngine.SqlServer, back.Engine);
    }

    [Fact]
    public void Unreadable_config_reads_as_nothing()
    {
        Assert.Null(InstallPlan.ReadExisting(null));
        Assert.Null(InstallPlan.ReadExisting("{ not json"));
    }

    [Fact]
    public void Finds_the_app_role_id_for_a_permission()
    {
        const string roles = """[{"value":"Application.Read.All","id":"9a5d68dd"},{"value":"Directory.Read.All","id":"7ab1d382"}]""";
        Assert.Equal("9a5d68dd", InstallPlan.AppRoleId(roles, "Application.Read.All"));
        Assert.Null(InstallPlan.AppRoleId(roles, "Nope.Read.All"));
    }

    [Fact]
    public void Msp_next_steps_point_to_onboarding()
        => Assert.Contains(InstallPlan.NextSteps(EditionChoice.Msp), s => s.Contains("Add client"));

    // A new database makes only the exact Admin email an Admin; signing in with any
    // other account lands on "no clients assigned". The last page must say which.
    [Theory]
    [InlineData(EditionChoice.Msp)]
    [InlineData(EditionChoice.Single)]
    public void Next_steps_name_the_admin_account_to_sign_in_with(EditionChoice edition)
    {
        var steps = InstallPlan.NextSteps(edition, "  samir@contoso.test ");
        Assert.Contains("samir@contoso.test", steps[0]);
        Assert.Contains("Viewer", steps[0]);
        Assert.DoesNotContain("  samir", steps[0]); // trimmed
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Next_steps_fall_back_to_the_generic_sign_in_line_without_an_email(string? email)
        => Assert.Equal("Open Vigil365 and sign in as the administrator.", InstallPlan.NextSteps(EditionChoice.Msp, email)[0]);

    // ── Re-running Setup: reuse this install's own app, keep what it does not own ──

    [Fact]
    public void Existing_config_names_its_app_registration()
    {
        var back = InstallPlan.ReadExisting("""{ "AzureAd": { "ClientId": "8F1C2B9A-0000-4000-8000-000000000001" } }""");
        Assert.Equal("8f1c2b9a-0000-4000-8000-000000000001", back!.ClientId);
        // The appsettings.json placeholder is not an app to look up.
        Assert.Null(InstallPlan.ReadExisting("""{ "AzureAd": { "ClientId": "YOUR_APP_CLIENT_ID" } }""")!.ClientId);
    }

    [Fact]
    public void Only_apps_named_exactly_vigil365_are_reuse_candidates()
    {
        // az ad app list --display-name is a starts-with match.
        const string list = """
            [ { "displayName": "Vigil365 (Pilot)", "appId": "pilot" },
              { "displayName": "Vigil365", "appId": "prod" },
              { "displayName": "vigil365", "appId": "lower" } ]
            """;
        Assert.Equal(new[] { "prod" }, InstallPlan.AppIdsNamedExactly(list, "Vigil365"));
        Assert.Empty(InstallPlan.AppIdsNamedExactly("", "Vigil365"));
    }

    [Fact]
    public void Patch_keeps_the_redirect_uris_another_install_relies_on()
    {
        const string app = """
            { "spa": { "redirectUris": [ "https://pilot.test" ] },
              "web": { "redirectUris": [ "https://pilot.test/consented" ] } }
            """;
        var json = InstallPlan.AppPatchJson(EditionChoice.Msp, "https://prod.test", "[]", "",
            InstallPlan.RedirectUris(app, "spa"), InstallPlan.RedirectUris(app, "web"));
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal(new[] { "https://pilot.test", "https://prod.test" },
            doc.GetProperty("spa").GetProperty("redirectUris").EnumerateArray().Select(u => u.GetString()));
        Assert.Equal(new[] { "https://pilot.test/consented", "https://prod.test/consented" },
            doc.GetProperty("web").GetProperty("redirectUris").EnumerateArray().Select(u => u.GetString()));

        // Re-running on the same server adds nothing.
        var again = JsonDocument.Parse(InstallPlan.AppPatchJson(EditionChoice.Single, "https://pilot.test", "[]", "",
            InstallPlan.RedirectUris(app, "spa"))).RootElement;
        Assert.Equal(1, again.GetProperty("spa").GetProperty("redirectUris").GetArrayLength());
    }

    [Theory]
    [InlineData(EditionChoice.Single, null, "AzureADMyOrg")]                    // new app
    [InlineData(EditionChoice.Single, "AzureADMyOrg", "AzureADMyOrg")]
    [InlineData(EditionChoice.Single, "AzureADMultipleOrgs", "AzureADMultipleOrgs")] // an MSP's app is never narrowed
    [InlineData(EditionChoice.Msp, null, "AzureADMultipleOrgs")]
    [InlineData(EditionChoice.Msp, "AzureADMyOrg", "AzureADMultipleOrgs")]      // convert to MSP
    public void A_reused_app_is_never_narrowed_to_single_tenant(EditionChoice edition, string? current, string expected)
    {
        Assert.Equal(expected, InstallPlan.SignInAudience(edition, current));
        var doc = JsonDocument.Parse(InstallPlan.AppPatchJson(edition, "https://pilot.test", "[]", "", currentAudience: current)).RootElement;
        Assert.Equal(expected, doc.GetProperty("signInAudience").GetString());
    }

    [Fact]
    public void A_single_pilot_sharing_an_msp_app_keeps_its_consent_redirect_and_audience()
    {
        const string mspApp = """
            { "signInAudience": "AzureADMultipleOrgs",
              "spa": { "redirectUris": [ "https://msp.test" ] },
              "web": { "redirectUris": [ "https://msp.test/consented" ] } }
            """;
        var doc = JsonDocument.Parse(InstallPlan.AppPatchJson(EditionChoice.Single, "https://pilot.test", "[]", "",
            InstallPlan.RedirectUris(mspApp, "spa"), InstallPlan.RedirectUris(mspApp, "web"), "AzureADMultipleOrgs")).RootElement;
        Assert.Equal("AzureADMultipleOrgs", doc.GetProperty("signInAudience").GetString());
        Assert.False(doc.TryGetProperty("web", out _)); // left as it is, /consented included
        Assert.Equal(new[] { "https://msp.test", "https://pilot.test" },
            doc.GetProperty("spa").GetProperty("redirectUris").EnumerateArray().Select(u => u.GetString()));
    }

    [Fact]
    public void A_server_that_does_not_share_another_install_s_app_names_its_own_after_the_host()
    {
        Assert.Equal("Vigil365 (VIGIL-PILOT)", InstallPlan.HostAppName("VIGIL-PILOT"));
        // ...which is not a candidate for another server's exact-name reuse.
        Assert.Empty(InstallPlan.AppIdsNamedExactly("""[ { "displayName": "Vigil365 (VIGIL-PILOT)", "appId": "pilot" } ]""", "Vigil365"));
    }

    [Fact]
    public void Rerun_keeps_operator_settings_and_replaces_what_setup_owns()
    {
        const string previous = """
            {
              // operator edits
              "Kestrel": { "Endpoints": { "Public": { "Url": "https://*:443", "Certificate": { "Path": "old.pfx", "Password": "p" } } } },
              "graph": { "ClientId": "old-app", "ClientSecret": "old", "TenantParallelism": 8 },
              "Database": { "Provider": "SqlServer", "SizeWarningBytes": 1000 },
              "Retention": { "ResolvedAlertsDays": 30 },
            }
            """;
        const string installer = """
            {
              "Kestrel": { "Endpoints": { "Public": { "Url": "https://*:443", "Certificate": { "Subject": "CN=vigil", "Store": "My" } } } },
              "Graph": { "ClientId": "new-app", "ClientSecret": "new" },
              "Database": { "Provider": "Postgres" }
            }
            """;
        var merged = JsonDocument.Parse(InstallPlan.MergeConfig(previous, installer)).RootElement;

        Assert.Equal(30, merged.GetProperty("Retention").GetProperty("ResolvedAlertsDays").GetInt32());
        Assert.Equal(1000, merged.GetProperty("Database").GetProperty("SizeWarningBytes").GetInt32());
        Assert.Equal("Postgres", merged.GetProperty("Database").GetProperty("Provider").GetString());

        // Merged into the existing section whatever its casing — .NET config would refuse two "Graph" keys.
        var graph = merged.EnumerateObject().Single(p => p.NameEquals("graph") || p.NameEquals("Graph")).Value;
        Assert.Equal("new-app", graph.GetProperty("ClientId").GetString());
        Assert.Equal("new", graph.GetProperty("ClientSecret").GetString());
        Assert.Equal(8, graph.GetProperty("TenantParallelism").GetInt32());

        // Kestrel is replaced whole: a stale Path beside the new Subject is a config Kestrel refuses.
        var cert = merged.GetProperty("Kestrel").GetProperty("Endpoints").GetProperty("Public").GetProperty("Certificate");
        Assert.False(cert.TryGetProperty("Path", out _));
        Assert.Equal("CN=vigil", cert.GetProperty("Subject").GetString());
    }

    [Fact]
    public void Missing_or_unreadable_previous_config_writes_the_installer_values()
    {
        const string installer = """{ "Edition": { "Mode": "Msp" } }""";
        foreach (var previous in new[] { null, "", "{ not json" })
            Assert.Equal("Msp", JsonDocument.Parse(InstallPlan.MergeConfig(previous, installer)).RootElement
                .GetProperty("Edition").GetProperty("Mode").GetString());
    }

    // ── Failure advice follows the step that failed, not words in the message ──

    [Theory]
    [InlineData(InstallStage.Service)]
    [InlineData(InstallStage.Files)]
    public void Late_failures_never_claim_nothing_was_installed(InstallStage stage)
    {
        var (_, remedy) = InstallPlan.FailureAdvice(stage);
        Assert.DoesNotContain("not been changed", remedy);
        Assert.DoesNotContain("Nothing has been", remedy);
    }

    [Fact]
    public void Each_step_gets_advice_about_that_step()
    {
        Assert.Contains("az logout", InstallPlan.FailureAdvice(InstallStage.SignIn).Remedy);
        Assert.DoesNotContain("SQL", InstallPlan.FailureAdvice(InstallStage.SignIn).Remedy);
        Assert.DoesNotContain("SQLEXPRESS", InstallPlan.FailureAdvice(InstallStage.DatabaseChoice).Remedy);
        Assert.Contains("PostgreSQL", InstallPlan.FailureAdvice(InstallStage.Postgres).Title);
        Assert.DoesNotContain("SQL Server", InstallPlan.FailureAdvice(InstallStage.Postgres).Remedy);
        Assert.Contains("Event Viewer", InstallPlan.FailureAdvice(InstallStage.Service).Remedy);
        foreach (var stage in Enum.GetValues<InstallStage>())
            Assert.False(string.IsNullOrWhiteSpace(InstallPlan.FailureAdvice(stage).Title));
    }

    // ── Who may read the secrets Setup writes (inst-3) ──

    private static (string, AclRights)[] Grants(RestrictedAcl acl) => acl.Grants.Select(g => (g.Sid, g.Rights)).ToArray();

    [Fact]
    public void The_config_file_is_readable_by_the_service_and_managed_only_by_admins_and_system()
    {
        // appsettings.Production.json holds the Graph secret: in MSP mode, the key to
        // every consenting client's data. Program Files lets every local user read.
        var acl = InstallPlan.ConfigFileAcl();

        Assert.True(acl.ProtectFromParent); // nothing inherited, so no "Users: Read"
        Assert.False(acl.InheritedByChildren);
        Assert.Equal(new[]
        {
            ("S-1-5-32-544", AclRights.FullControl), // BUILTIN\Administrators
            ("S-1-5-18", AclRights.FullControl),     // SYSTEM
            ("S-1-5-19", AclRights.Read),            // LOCAL SERVICE, the service account
        }, Grants(acl));
    }

    [Fact]
    public void The_data_folder_is_writable_by_the_service_for_everything_below_it_and_closed_to_others()
    {
        // %ProgramData%\Vigil365: the key ring that decrypts every stored secret, and logs.
        var acl = InstallPlan.DataFolderAcl();

        Assert.True(acl.ProtectFromParent);
        Assert.True(acl.InheritedByChildren); // keys\ and logs\ and every file in them
        Assert.Equal(new[]
        {
            ("S-1-5-32-544", AclRights.FullControl),
            ("S-1-5-18", AclRights.FullControl),
            ("S-1-5-19", AclRights.Modify),
        }, Grants(acl));
    }

    [Fact]
    public void No_installer_acl_lets_anyone_else_in_or_the_service_rewrite_it()
    {
        string[] others = ["S-1-1-0", "S-1-5-11", "S-1-5-32-545", "S-1-5-4"]; // Everyone, Authenticated Users, Users, Interactive
        foreach (var acl in new[] { InstallPlan.ConfigFileAcl(), InstallPlan.DataFolderAcl() })
        {
            Assert.DoesNotContain(acl.Grants, g => others.Contains(g.Sid));
            Assert.DoesNotContain(acl.Grants, g => g.Sid == InstallPlan.LocalServiceSid && g.Rights == AclRights.FullControl);
        }
    }

    [Fact]
    public void Setup_writes_its_config_and_data_folder_only_through_the_restricted_acls()
    {
        var source = MainWindowSource();
        Assert.Contains("RestrictAccess(new FileInfo(path), InstallPlan.ConfigFileAcl())", source);
        Assert.Contains("RestrictAccess(new DirectoryInfo(dataDir), InstallPlan.DataFolderAcl())", source);
        // The config and its .bak are written by WriteRestrictedFile, never directly.
        Assert.Equal(2, Regex.Matches(source, @"WriteRestrictedFile\(configPath").Count);
        Assert.DoesNotMatch(@"File\.(WriteAllText|Copy|Move)\(\s*configPath", source);
    }

    // ── Replacing the files of a running service (inst-8) ──

    private const string ScRunning = """

        SERVICE_NAME: Vigil365
                TYPE               : 10  WIN32_OWN_PROCESS
                STATE              : 4  RUNNING
                                        (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)
                WIN32_EXIT_CODE    : 0  (0x0)
                SERVICE_EXIT_CODE  : 0  (0x0)
                CHECKPOINT         : 0x0
                WAIT_HINT          : 0x0
        """;

    [Theory]
    [InlineData("4  RUNNING", ServiceState.Running)]
    [InlineData("1  STOPPED", ServiceState.Stopped)]
    [InlineData("3  STOP_PENDING", ServiceState.Changing)]   // asked to stop, still holding its files
    [InlineData("2  START_PENDING", ServiceState.Changing)]
    [InlineData("7  PAUSED", ServiceState.Changing)]
    public void Reads_the_service_state_from_sc_query(string state, ServiceState expected)
        => Assert.Equal(expected, InstallPlan.ParseServiceState(ScRunning.Replace("4  RUNNING", state)));

    [Theory]
    [InlineData("[SC] EnumQueryServicesStatus:OpenService FAILED 1060:\r\n\r\nThe specified service does not exist as an installed service.\r\n")]
    [InlineData("")]
    [InlineData(null)]
    public void No_state_means_not_installed(string? output)
        => Assert.Equal(ServiceState.NotInstalled, InstallPlan.ParseServiceState(output));

    [Fact]
    public void State_is_read_from_the_state_name_not_the_label()
    {
        // A translated label must not read as "not installed".
        Assert.Equal(ServiceState.Running, InstallPlan.ParseServiceState(ScRunning.Replace("STATE    ", "STATUS   ")));
        // The TYPE line's "WIN32_OWN_PROCESS" is not a state.
        Assert.Equal(ServiceState.NotInstalled, InstallPlan.ParseServiceState("        TYPE               : 10  WIN32_OWN_PROCESS"));
    }

    [Theory]
    [InlineData(ServiceState.NotInstalled, true)]  // a first install
    [InlineData(ServiceState.Stopped, true)]
    [InlineData(ServiceState.Changing, false)]     // STOP_PENDING: overwriting now fails on a locked file
    [InlineData(ServiceState.Running, false)]
    public void Files_are_replaced_only_once_the_service_has_stopped(ServiceState state, bool replaceable)
        => Assert.Equal(replaceable, InstallPlan.FilesReplaceable(state));

    [Theory]
    [InlineData(ServiceState.Running, true)]       // monitoring was up: bring it back
    [InlineData(ServiceState.Stopped, false)]      // stopped on purpose: leave it
    [InlineData(ServiceState.NotInstalled, false)]
    [InlineData(ServiceState.Changing, false)]
    public void A_failed_upgrade_restarts_only_a_service_that_was_running(ServiceState before, bool restart)
        => Assert.Equal(restart, InstallPlan.RestartAfterFailedUpgrade(before));

    [Fact]
    public void Setup_waits_for_the_stop_and_restarts_on_any_failure_to_replace_the_files()
    {
        var source = MainWindowSource();
        var method = source[source.IndexOf("private async Task InstallApplicationFiles()", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("private void SetupService()", StringComparison.Ordinal)];

        // The state is read before "sc stop", the wait sits inside the try, so a stop
        // that times out restarts the service too, and the catch asks InstallPlan.
        var read = method.IndexOf("InstallPlan.ParseServiceState(QueryService(", StringComparison.Ordinal);
        var stop = method.IndexOf("RunCommand(\"sc\", \"stop Vigil365\")", StringComparison.Ordinal);
        var tryAt = method.IndexOf("try", stop, StringComparison.Ordinal);
        var wait = method.IndexOf("await WaitForServiceStoppedAsync(", StringComparison.Ordinal);
        var extract = method.IndexOf("ExtractToFile(", StringComparison.Ordinal);
        var catchAt = method.IndexOf("catch", extract, StringComparison.Ordinal);
        Assert.True(read >= 0 && read < stop && stop < tryAt && tryAt < wait && wait < extract && extract < catchAt,
            "InstallApplicationFiles: read state, stop, then wait and extract inside one try");
        Assert.Contains("InstallPlan.RestartAfterFailedUpgrade(", method[catchAt..]);
        Assert.Contains("InstallPlan.FilesReplaceable(", source);
    }

    [Fact]
    public void The_collector_secret_is_minted_only_after_the_files_are_in_place()
    {
        // Minting it first left a live two-year secret on the app registration
        // whenever a locked file failed the run, with nothing configured to use it.
        var source = MainWindowSource();
        var files = source.IndexOf("await InstallApplicationFiles();", StringComparison.Ordinal);
        var secretCalls = Regex.Matches(source, @"(?<!void )\bCreateCollectorSecret\b(?!\(\)\s*\{)").Cast<Match>().ToList();

        Assert.True(files >= 0, "InstallApplicationFiles is no longer awaited by the install run");
        Assert.Single(secretCalls);
        Assert.True(secretCalls[0].Index > files, "CreateCollectorSecret must run after InstallApplicationFiles");
    }

    private static string MainWindowSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "graph-permissions.json"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new DirectoryNotFoundException("repo root (graph-permissions.json) not found");
        return File.ReadAllText(Path.Combine(root, "src", "M365SecurityDashboard.GuiInstaller", "MainWindow.xaml.cs"));
    }
}
