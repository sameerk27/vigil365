using System.Text.Json;
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
}
