using System.Text.Json;
using M365SecurityDashboard.Api.Services;
using Xunit;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// alerts_v2 evidence carries "@odata.type" (mailboxEvidence, userEvidence, analyzedMessageEvidence…),
/// not "entityType", so "Top targeted users" on the Email page was always empty.
/// </summary>
public class EmailEvidenceTests
{
    private static string? Of(string alertJson) => EmailEvidence.TargetedUpn(JsonDocument.Parse(alertJson).RootElement);

    [Theory]
    [InlineData("""{"evidence":[{"@odata.type":"#microsoft.graph.security.mailboxEvidence","upn":"ana@contoso.com","primaryAddress":"ana@contoso.com"}]}""", "ana@contoso.com")]
    [InlineData("""{"evidence":[{"@odata.type":"#microsoft.graph.security.mailboxEvidence","upn":null,"primaryAddress":"shared@contoso.com"}]}""", "shared@contoso.com")]
    [InlineData("""{"evidence":[{"@odata.type":"#microsoft.graph.security.userEvidence","userAccount":{"userPrincipalName":"bob@contoso.com"}}]}""", "bob@contoso.com")]
    [InlineData("""{"evidence":[{"@odata.type":"#microsoft.graph.security.ipEvidence"},{"@odata.type":"#microsoft.graph.security.analyzedMessageEvidence","recipientEmailAddress":"eva@contoso.com"}]}""", "eva@contoso.com")]
    public void The_evidence_Graph_sends_names_the_targeted_mailbox(string alert, string expected) => Assert.Equal(expected, Of(alert));

    [Theory]
    [InlineData("""{"evidence":[{"entityType":"User","userAccount":{"userPrincipalName":"ana@contoso.com"}}]}""", "ana@contoso.com")]
    [InlineData("""{"evidence":[{"entityType":"Mailbox","mailbox":{"userPrincipalName":"bob@contoso.com"}}]}""", "bob@contoso.com")]
    public void The_entity_shape_still_works(string alert, string expected) => Assert.Equal(expected, Of(alert));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"evidence":[{"@odata.type":"#microsoft.graph.security.mailClusterEvidence"}]}""")]
    [InlineData("""{"evidence":[{"@odata.type":"#microsoft.graph.security.userEvidence","userAccount":{"userPrincipalName":""}}]}""")]
    public void No_mailbox_no_user(string alert) => Assert.Null(Of(alert));
}
