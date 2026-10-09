using System.Text.Json;
using M365SecurityDashboard.Api.Models;

namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// One answer per client to "is it working, and if not, what do I do?" The collector
/// records what happened (<see cref="CollectionStatus"/>, source failures); this turns
/// that into the states an MSP operator acts on. Pure, so every state is unit-tested.
/// </summary>
public static class ClientHealth
{
    // Stored in ClientTenant.LastCollectionStatus alongside the CollectionStatus names.
    public const string Failed = nameof(CollectionStatus.Failed);
    public const string Partial = "Partial";
    public const string Unreadable = "Unreadable";
    public const string WaitingForConsent = "WaitingForConsent";

    // Reported states (ClientStatus.State). Stable strings: the UI and the digest key on them.
    public const string StateDeactivated = "Deactivated";
    public const string StateCredentialsUnreadable = "CredentialsUnreadable";
    public const string StateWaitingForConsent = "WaitingForConsent";
    public const string StateBackingOff = "BackingOff";
    public const string StateFailed = "Failed";
    public const string StatePartial = "Partial";
    public const string StateConnected = "Connected";
    public const string StateNotCollectedYet = "NotCollectedYet";

    /// <summary>
    /// What a run with source failures means for the client. No failures: completed.
    /// Some failures: partly connected, and the error names the failing sources (up to
    /// three) so the operator knows which permission or licence to look at.
    /// </summary>
    public static (string Status, string? Error) OutcomeOf(int sourceFailures, string? failureDetailsJson)
    {
        if (sourceFailures == 0) return (nameof(CollectionStatus.Completed), null);

        var names = FailingSources(failureDetailsJson);
        var listed = names.Count == 0
            ? ""
            : ": " + string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "");
        return (Partial, $"{sourceFailures} source(s) failing{listed}. Open Collection Health for each error.");
    }

    /// <summary>
    /// True when every failed source failed only because Microsoft has not finished setting
    /// up the app in this tenant yet (right after consent). Such a run is not a failure.
    /// </summary>
    public static bool AllFailuresAreConsentPending(string? failureDetailsJson)
    {
        var errors = FailureErrors(failureDetailsJson);
        return errors.Count > 0 && errors.All(e => e.Contains(GraphErrorHint.NotYetProvisionedMessage, StringComparison.Ordinal));
    }

    /// <summary>The state an operator acts on, from the client's stored facts.</summary>
    public static ClientStatus Of(ClientStatusInput i)
    {
        if (!i.IsActive) return new(StateDeactivated, "neutral", "Deactivated");
        if (i.SecretUnreadable || i.LastCollectionStatus == Unreadable)
            return new(StateCredentialsUnreadable, "error", "Credentials unreadable: re-enter them");
        if (!i.Configured) return new(StateWaitingForConsent, "neutral", "Waiting for consent");
        if (i.LastCollectionStatus == WaitingForConsent)
            return new(StateWaitingForConsent, "neutral", "Waiting for Microsoft to finish setup");
        if (i.NextCollectionAfter is { } next && next > i.Now)
            return new(StateBackingOff, "warning", $"Backing off until {next.ToLocalTime():HH:mm}");
        if (i.LastCollectionStatus == nameof(CollectionStatus.Failed))
            return new(StateFailed, "error", "Failed: see the last error");
        if (i.LastCollectionStatus == Partial)
            return new(StatePartial, "warning", "Partly connected: some sources failing");
        if (i.LastCollectionStatus == nameof(CollectionStatus.Completed))
            return new(StateConnected, "good", "Connected");
        return new(StateNotCollectedYet, "neutral", "Not collected yet");
    }

    private static List<string> FailingSources(string? json)
    {
        var names = new List<string>();
        foreach (var e in Entries(json))
            if (e.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String) names.Add(s.GetString()!);
        return names;
    }

    private static List<string> FailureErrors(string? json)
    {
        var errors = new List<string>();
        foreach (var e in Entries(json))
            errors.Add(e.TryGetProperty("error", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "");
        return errors;
    }

    private static IEnumerable<JsonElement> Entries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in doc.RootElement.EnumerateArray()) yield return e.Clone();
        }
    }
}

/// <summary>The facts about a client that decide its state.</summary>
public sealed record ClientStatusInput(
    bool IsActive,
    bool Configured,
    bool SecretUnreadable,
    string? LastCollectionStatus,
    DateTimeOffset? NextCollectionAfter,
    DateTimeOffset Now);

/// <summary>A client's state, how urgent it is ("good", "warning", "error", "neutral") and a sentence for the operator.</summary>
public sealed record ClientStatus(string State, string Tone, string Label);

/// <summary>
/// Thrown when a collection for this client is already running (a manual run, or the
/// other pass of the scheduler). That is not a failure of the client.
/// </summary>
public sealed class CollectionBusyException() : InvalidOperationException("A collection run is already in progress for this tenant.");
