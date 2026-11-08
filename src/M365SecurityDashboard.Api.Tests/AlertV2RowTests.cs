using System.Text.Json;
using M365SecurityDashboard.Api.Endpoints;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// Six dashboard sections read alerts_v2 through AlertV2Row. The SPA lower-cases severity and
/// status, so these must never reach it as null, and the JSON names must stay what the SPA reads.
/// </summary>
public class AlertV2RowTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_null_or_missing_severity_and_status_become_unknown()
    {
        var row = AlertV2Row.From(Parse("""{"id":"a1","severity":null}"""));
        Assert.Equal("unknown", row.Severity);
        Assert.Equal("unknown", row.Status);
    }

    [Fact]
    public void Wrongly_typed_fields_are_dropped_instead_of_failing_the_section()
    {
        var row = AlertV2Row.From(Parse("""{"id":7,"title":{"x":1},"description":["d"],"mitreTechniques":"T1078","severity":"high"}"""));
        Assert.Null(row.Id);
        Assert.Null(row.Title);
        Assert.Null(row.Description);
        Assert.Empty(row.MitreTechniques);
        Assert.Equal("high", row.Severity);
    }

    [Fact]
    public void Serialises_to_the_names_the_spa_reads()
    {
        var row = AlertV2Row.From(Parse("""
            {"id":"a1","title":"t","severity":"high","status":"new","category":"c","serviceSource":"s",
             "createdDateTime":"2026-10-04T07:47:08Z","description":"d","alertWebUrl":"https://x","mitreTechniques":["T1078"]}
            """));
        var json = JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var name in new[] { "\"id\"", "\"title\"", "\"severity\"", "\"status\"", "\"category\"", "\"serviceSource\"",
                                     "\"createdDateTime\"", "\"description\"", "\"alertWebUrl\"", "\"mitreTechniques\":[\"T1078\"]" })
            Assert.Contains(name, json);
    }

    [Fact]
    public void CountBy_buckets_missing_keys_as_unknown()
    {
        var rows = new[]
        {
            AlertV2Row.From(Parse("""{"severity":"high","category":"x"}""")),
            AlertV2Row.From(Parse("""{"severity":"high"}""")),
            AlertV2Row.From(Parse("""{}""")),
        };
        Assert.Equal(new Dictionary<string, int> { ["high"] = 2, ["unknown"] = 1 }, AlertV2Row.CountBy(rows, r => r.Severity));
        Assert.Equal(new Dictionary<string, int> { ["x"] = 1, ["unknown"] = 2 }, AlertV2Row.CountBy(rows, r => r.Category));
    }

    [Fact]
    public void Insider_risk_is_picked_by_detection_source()
    {
        var items = new[]
        {
            Parse("""{"id":"a","detectionSource":"microsoftInsiderRiskManagement","severity":"high"}"""),
            Parse("""{"id":"b","detectionSource":"antivirus"}"""),
            Parse("""{"id":"c"}"""),
        };
        var (alerts, note) = AlertV2Row.PickInsiderRisk(items);
        Assert.Equal("a", Assert.Single(alerts).Id);
        Assert.Null(note);
    }

    [Fact]
    public void A_full_page_of_alerts_says_older_insider_risk_alerts_may_be_missing()
    {
        var items = Enumerable.Repeat(Parse("""{"detectionSource":"antivirus"}"""), AlertV2Row.InsiderRiskScanLimit).ToList();
        var (alerts, note) = AlertV2Row.PickInsiderRisk(items);
        Assert.Empty(alerts);
        Assert.Contains("newest 200", note);
    }

    private const string Ns = "#microsoft.graph.security.";

    [Theory]
    [InlineData("mailboxEvidence", """{"upn":"a@x.test","primaryAddress":"b@x.test"}""", "a@x.test")]
    [InlineData("mailboxEvidence", """{"upn":"","userAccount":{"userPrincipalName":"c@x.test"}}""", "c@x.test")]
    [InlineData("mailboxEvidence", """{"primaryAddress":"b@x.test"}""", "b@x.test")]
    [InlineData("analyzedMessageEvidence", """{"recipientEmailAddress":"d@x.test"}""", "d@x.test")]
    [InlineData("userEvidence", """{"userAccount":{"userPrincipalName":"e@x.test"}}""", "e@x.test")]
    public void Targeted_user_is_read_from_each_evidence_type(string type, string fields, string expected)
    {
        var body = fields.TrimStart('{');
        var alert = Parse($$"""{"evidence":[{"@odata.type":"{{Ns}}{{type}}",{{body}}]}""");
        Assert.Equal(expected, AlertV2Row.TargetedUser(alert));
    }

    [Fact]
    public void The_mailbox_wins_over_the_user_who_reported_the_message()
    {
        var alert = Parse($$$"""
            {"evidence":[
              {"@odata.type":"{{{Ns}}}userEvidence","userAccount":{"userPrincipalName":"reporter@x.test"}},
              {"@odata.type":"{{{Ns}}}mailboxEvidence","upn":"victim@x.test"}]}
            """);
        Assert.Equal("victim@x.test", AlertV2Row.TargetedUser(alert));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"evidence":"none"}""")]
    [InlineData("""{"evidence":[1,null,{"@odata.type":"#microsoft.graph.security.ipEvidence","ipAddress":"1.1.1.1"}]}""")]
    public void No_usable_evidence_means_no_targeted_user(string json) =>
        Assert.Null(AlertV2Row.TargetedUser(Parse(json)));
}
