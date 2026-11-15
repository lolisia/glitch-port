namespace GlitchPort.Relay.RateLimit;

/// <summary>
/// In-memory token-bucket limiter for the Relay ingest path. Every project has
/// a bucket of <c>Relay:RateLimit:PerProjectPerMinute</c> tokens that refills
/// continuously over a minute; a project key whose Processor configuration
/// carries a non-null <c>rate_limit_per_minute</c> additionally gets its own
/// bucket. A request is admitted only when every applicable bucket holds a
/// token, and rejected requests consume nothing from any bucket.
/// </summary>
/// <remarks>
/// <para>
/// The limiter is registered as a process-wide singleton. Every bucket lives in
/// memory and is guarded by one lock, so concurrent requests can never admit
/// more tokens than a bucket holds. Running several Relay instances therefore
/// enforces the limit per instance; a shared limiter is out of scope for the
/// in-memory MVP.
/// </para>
/// <para>
/// The clock is a <see cref="TimeProvider"/>, which lets tests advance time
/// deterministically instead of sleeping.
/// </para>
/// </remarks>
public sealed class ProjectRateLimiter
{
    /// <summary>Window over which configured limits are refilled, in seconds.</summary>
    public const double RefillWindowSeconds = 60;

    /// <summary>
    /// Tolerance used when comparing accumulated fractional tokens against a
    /// whole token, so a bucket refilled by exactly one window is not rejected
    /// because of floating-point rounding.
    /// </summary>
    private const double TokenEpsilon = 1e-9;

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _perProjectPerMinute;
    private readonly Dictionary<long, Bucket> _projectBuckets = [];
    private readonly Dictionary<(long ProjectId, string Key), Bucket> _keyBuckets = [];

    /// <summary>Creates the limiter from the Relay options.</summary>
    /// <param name="options">Static Relay configuration.</param>
    /// <param name="timeProvider">Clock used to refill the buckets.</param>
    public ProjectRateLimiter(RelayOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _perProjectPerMinute = Math.Max(0, options.RateLimitPerProjectPerMinute);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Per-project limit in requests per minute. Zero disables the project
    /// bucket; only per-key limits apply in that case.
    /// </summary>
    public int PerProjectPerMinute => _perProjectPerMinute;

    /// <summary>
    /// Tries to admit one request for <paramref name="projectId"/>.
    /// <paramref name="rateLimitPerMinute"/> is the optional limit of the
    /// authorized public key: <see langword="null"/> means the key has no limit
    /// of its own and zero means the key limit is unlimited.
    /// </summary>
    /// <returns>
    /// <see cref="RateLimitDecision.Allowed"/> when the request was admitted, or
    /// a rejection carrying the number of seconds to wait.
    /// </returns>
    public RateLimitDecision Acquire(
        long projectId,
        string? publicKey,
        int? rateLimitPerMinute)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            var projectBucket = _perProjectPerMinute > 0
                ? GetOrCreate(_projectBuckets, projectId, _perProjectPerMinute, now)
                : null;

            Bucket? keyBucket = null;
            if (rateLimitPerMinute is > 0 && !string.IsNullOrEmpty(publicKey))
            {
                keyBucket = GetOrCreate(
                    _keyBuckets,
                    (projectId, publicKey),
                    rateLimitPerMinute.Value,
                    now);
            }

            if (projectBucket is null && keyBucket is null)
            {
                return RateLimitDecision.Allowed;
            }

            Refill(projectBucket, now);
            Refill(keyBucket, now);

            // Check every bucket before consuming anything, so a rejection never
            // takes a token from a bucket that still had one.
            if (HasToken(projectBucket) && HasToken(keyBucket))
            {
                Consume(projectBucket);
                Consume(keyBucket);
                return RateLimitDecision.Allowed;
            }

            return RateLimitDecision.Rejected(SecondsUntilNextToken(projectBucket, keyBucket));
        }
    }

    private static Bucket GetOrCreate<TKey>(
        Dictionary<TKey, Bucket> buckets,
        TKey key,
        int capacity,
        DateTimeOffset now)
        where TKey : notnull
    {
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket(capacity, now);
            buckets[key] = bucket;
            return bucket;
        }

        // The Processor can change a key's limit between refreshes. Adopt the
        // new capacity while keeping the tokens already earned.
        bucket.SetCapacity(capacity);
        return bucket;
    }

    private static void Refill(Bucket? bucket, DateTimeOffset now)
    {
        if (bucket is null)
        {
            return;
        }

        if (now < bucket.UpdatedAt)
        {
            // A clock that moved backwards must neither freeze the bucket nor
            // grant tokens: rebase it and wait for real time to pass again.
            bucket.UpdatedAt = now;
            return;
        }

        var elapsedSeconds = (now - bucket.UpdatedAt).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        bucket.Tokens = Math.Min(
            bucket.Capacity,
            bucket.Tokens + (elapsedSeconds * bucket.TokensPerSecond));
        bucket.UpdatedAt = now;
    }

    private static bool HasToken(Bucket? bucket) =>
        bucket is null || bucket.Tokens + TokenEpsilon >= 1;

    private static void Consume(Bucket? bucket)
    {
        if (bucket is not null)
        {
            bucket.Tokens = Math.Max(0, bucket.Tokens - 1);
        }
    }

    private static int SecondsUntilNextToken(Bucket? first, Bucket? second)
    {
        var waitSeconds = Math.Max(WaitFor(first), WaitFor(second));

        // Ceiling, so a client that waits the advertised time always finds a
        // token. Never below one second.
        return Math.Max(1, (int)Math.Ceiling(waitSeconds));

        static double WaitFor(Bucket? bucket) =>
            bucket is not null && bucket.Tokens + TokenEpsilon < 1
                ? (1 - bucket.Tokens) / bucket.TokensPerSecond
                : 0d;
    }

    /// <summary>One token bucket; every field is guarded by <see cref="_gate"/>.</summary>
    private sealed class Bucket
    {
        public Bucket(int capacity, DateTimeOffset now)
        {
            Capacity = capacity;
            Tokens = capacity;
            UpdatedAt = now;
        }

        public int Capacity { get; private set; }

        public double Tokens { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public double TokensPerSecond => Capacity / RefillWindowSeconds;

        public void SetCapacity(int capacity)
        {
            if (capacity == Capacity)
            {
                return;
            }

            Capacity = capacity;
            Tokens = Math.Min(Tokens, capacity);
        }
    }
}
