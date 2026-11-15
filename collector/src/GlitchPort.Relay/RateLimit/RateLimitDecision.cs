namespace GlitchPort.Relay.RateLimit;

/// <summary>
/// Outcome of a rate-limit admission check. When <see cref="IsAllowed"/> is
/// <see langword="false"/>, <see cref="RetryAfterSeconds"/> is the number of
/// whole seconds a client should wait before retrying.
/// </summary>
public readonly record struct RateLimitDecision(bool IsAllowed, int RetryAfterSeconds)
{
    /// <summary>An admitted request; no wait is required.</summary>
    public static RateLimitDecision Allowed { get; } = new(true, 0);

    /// <summary>
    /// A rejected request. <paramref name="retryAfterSeconds"/> is clamped to at
    /// least one second so a client always receives a useful <c>Retry-After</c>
    /// value.
    /// </summary>
    public static RateLimitDecision Rejected(int retryAfterSeconds) =>
        new(false, Math.Max(1, retryAfterSeconds));
}
