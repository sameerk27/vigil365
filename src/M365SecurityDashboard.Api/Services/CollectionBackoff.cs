namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Per-tenant collection backoff. A client whose collection keeps failing
/// (expired consent, revoked app, persistent throttling) is retried less and
/// less often — interval × 2^failures, capped — so one broken client neither
/// burns the MSP's Graph budget nor delays the healthy ones. A single success
/// resets it. Pure and unit-tested.
/// </summary>
public static class CollectionBackoff
{
    /// <summary>When to try this tenant again after its Nth consecutive failure.</summary>
    public static DateTimeOffset NextAttempt(DateTimeOffset now, int consecutiveFailures, TimeSpan interval, TimeSpan maxBackoff)
    {
        if (consecutiveFailures <= 0) return now;
        var exponent = Math.Min(consecutiveFailures, 16); // 2^16 is already far past any sane cap
        var delayTicks = interval.Ticks * (1L << exponent);
        var delay = delayTicks <= 0 || delayTicks > maxBackoff.Ticks ? maxBackoff : TimeSpan.FromTicks(delayTicks);
        return now + delay;
    }

    public static bool IsDue(DateTimeOffset now, DateTimeOffset? nextAttempt) => nextAttempt is null || nextAttempt <= now;
}
