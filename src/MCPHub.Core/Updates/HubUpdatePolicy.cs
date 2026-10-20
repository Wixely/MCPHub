namespace MCPHub.Core.Updates;

/// <summary>
/// When MCPHub looks for a newer release on its own. Checks are cached, not repeated on every launch: an
/// automatic check happens only when the last one is older than <see cref="AutoCheckInterval"/>. GitHub
/// allows sixty unauthenticated requests an hour, and a hub that is restarted often would otherwise spend
/// them on the same answer.
/// </summary>
public static class HubUpdatePolicy
{
    /// <summary>How old the last check has to be before another one runs unasked.</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(48);

    /// <summary>
    /// Whether an automatic check is due. Never having checked counts as due, and so does a last check
    /// that sits in the future — a clock that was put back should not silence checks until it catches up.
    /// </summary>
    public static bool IsCheckDue(DateTimeOffset? lastCheckedAt, DateTimeOffset now)
    {
        if (lastCheckedAt is null)
            return true;

        var age = now - lastCheckedAt.Value;
        return age >= AutoCheckInterval || age < -AutoCheckInterval;
    }

    /// <summary>When the next automatic check falls, or <see langword="null"/> when one is due now.</summary>
    public static DateTimeOffset? NextCheckAt(DateTimeOffset? lastCheckedAt, DateTimeOffset now)
        => IsCheckDue(lastCheckedAt, now) ? null : lastCheckedAt + AutoCheckInterval;
}
