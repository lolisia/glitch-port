namespace GlitchPort.Relay.Config;

/// <summary>
/// Holds the latest Processor project configuration and authorizes public keys
/// against it. Unknown (project id, key) pairs are negatively cached for
/// <c>Relay:UnknownKeyCacheSeconds</c>; a successful Processor refresh clears
/// that cache so keys created in the meantime start working immediately.
/// </summary>
/// <remarks>
/// The Processor snapshot is the only key source: there is no static
/// configuration fallback. Before the first successful refresh the provider
/// reports the <c>none</c> source and the ingest endpoints answer
/// <c>503 Service Unavailable</c> so clients retry instead of losing events.
/// <para>
/// The negative cache is a best-effort optimization that only avoids repeating
/// lookups for pairs that are known to be unknown. Unknown keys arrive straight
/// from a public request header, so they are attacker controlled: the cache
/// never stores keys longer than <see cref="MaxUnknownKeyCacheKeyLength"/>
/// characters, never grows beyond <see cref="MaxUnknownKeyCacheEntries"/>
/// entries and is never scanned per request. Authentication itself does not
/// depend on the cache.
/// </para>
/// </remarks>
public sealed class ProjectConfigProvider
{
    /// <summary>Largest number of unknown-key pairs kept in the negative cache.</summary>
    public const int MaxUnknownKeyCacheEntries = 4096;

    /// <summary>
    /// Longest public key, in characters, that is kept in the negative cache.
    /// Real ingest keys are 32 hex characters; longer values are never cached so
    /// a request cannot pin an arbitrarily large string in memory.
    /// </summary>
    public const int MaxUnknownKeyCacheKeyLength = 128;

    /// <summary>Source reported before the first successful Processor refresh.</summary>
    public const string NotLoadedSource = "none";

    /// <summary>Source reported after at least one successful Processor refresh.</summary>
    public const string ProcessorSource = "processor";

    private readonly object _gate = new();
    private readonly TimeSpan _unknownKeyCacheDuration;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<(long ProjectId, string Key), DateTimeOffset> _unknownKeys = [];

    private ProjectConfigSnapshot _snapshot = ProjectConfigSnapshot.Empty;
    private DateTimeOffset? _lastRefresh;
    private bool _configurationLoaded;
    private long _unknownKeyCacheHits;

    /// <summary>Creates a provider from the Relay options.</summary>
    /// <param name="options">Relay configuration.</param>
    /// <param name="timeProvider">Clock used for negative-cache expiry.</param>
    public ProjectConfigProvider(RelayOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _unknownKeyCacheDuration = TimeSpan.FromSeconds(options.UnknownKeyCacheSeconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The latest Processor configuration, or the empty snapshot.</summary>
    public ProjectConfigSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>
    /// Whether a successful Processor refresh (HTTP <c>200</c> or <c>304</c>)
    /// has happened. The ingest endpoints answer <c>503</c> until this is true,
    /// and a presented key is deliberately not negatively cached in that state.
    /// </summary>
    public bool IsConfigurationLoaded
    {
        get
        {
            lock (_gate)
            {
                return _configurationLoaded;
            }
        }
    }

    /// <summary>
    /// Either <c>none</c> before the first successful refresh or
    /// <c>processor</c> afterwards.
    /// </summary>
    public string Source
    {
        get
        {
            lock (_gate)
            {
                return _configurationLoaded ? ProcessorSource : NotLoadedSource;
            }
        }
    }

    /// <summary>When the last successful refresh happened, if any.</summary>
    public DateTimeOffset? LastRefresh
    {
        get
        {
            lock (_gate)
            {
                return _lastRefresh;
            }
        }
    }

    /// <summary>Number of projects in the latest Processor configuration.</summary>
    public int ProjectCount
    {
        get
        {
            lock (_gate)
            {
                return _snapshot.ProjectCount;
            }
        }
    }

    /// <summary>Number of pairs currently held by the unknown-key negative cache.</summary>
    public int UnknownKeyCacheCount
    {
        get
        {
            lock (_gate)
            {
                return _unknownKeys.Count;
            }
        }
    }

    /// <summary>
    /// Number of authorizations that were answered from the unknown-key negative
    /// cache instead of the Processor configuration.
    /// </summary>
    public long UnknownKeyCacheHits
    {
        get
        {
            lock (_gate)
            {
                return _unknownKeyCacheHits;
            }
        }
    }

    /// <summary>Returns a consistent view of the health information.</summary>
    public ProjectConfigStatus GetStatus()
    {
        lock (_gate)
        {
            return new ProjectConfigStatus(
                _configurationLoaded ? ProcessorSource : NotLoadedSource,
                _lastRefresh,
                _snapshot.ProjectCount);
        }
    }

    /// <summary>
    /// Authorizes <paramref name="publicKey"/> for <paramref name="projectId"/>
    /// against the latest Processor configuration.
    /// </summary>
    public PublicKeyAuthorization? Authorize(long projectId, string? publicKey)
    {
        if (string.IsNullOrEmpty(publicKey))
        {
            return null;
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_unknownKeyCacheDuration > TimeSpan.Zero
                && _unknownKeys.TryGetValue((projectId, publicKey), out var expiresAt))
            {
                if (expiresAt > now)
                {
                    _unknownKeyCacheHits++;
                    return null;
                }

                _unknownKeys.Remove((projectId, publicKey));
            }

            if (_snapshot.TryGetRateLimit(projectId, publicKey, out var cachedRateLimit))
            {
                return new PublicKeyAuthorization(projectId, publicKey, cachedRateLimit);
            }

            RememberUnknownKey(projectId, publicKey, now);
            return null;
        }
    }

    /// <summary>
    /// Replaces the cached configuration after a successful Processor response
    /// and clears the unknown-key negative cache.
    /// </summary>
    public void ApplySnapshot(ProjectConfigSnapshot snapshot, DateTimeOffset refreshedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            _snapshot = snapshot;
            _lastRefresh = refreshedAt;
            _configurationLoaded = true;
            _unknownKeys.Clear();
        }
    }

    /// <summary>
    /// Records a successful conditional refresh (<c>304 Not Modified</c>) that
    /// keeps the current snapshot and clears the unknown-key negative cache.
    /// </summary>
    public void MarkRefreshed(DateTimeOffset refreshedAt)
    {
        lock (_gate)
        {
            _lastRefresh = refreshedAt;
            _configurationLoaded = true;
            _unknownKeys.Clear();
        }
    }

    private void RememberUnknownKey(long projectId, string publicKey, DateTimeOffset now)
    {
        if (_unknownKeyCacheDuration <= TimeSpan.Zero
            || publicKey.Length > MaxUnknownKeyCacheKeyLength)
        {
            return;
        }

        if (_unknownKeys.Count >= MaxUnknownKeyCacheEntries)
        {
            // The cache only saves lookups and every new key is attacker
            // controlled. Dropping it wholesale is O(1) and keeps memory
            // bounded; scanning it under the lock would stall every request.
            // A successful refresh clears it anyway.
            _unknownKeys.Clear();
        }

        _unknownKeys[(projectId, publicKey)] = now + _unknownKeyCacheDuration;
    }
}

/// <summary>Health information about the active project configuration.</summary>
/// <param name="Source">Either <c>none</c> before the first successful refresh or <c>processor</c> afterwards.</param>
/// <param name="LastRefresh">Time of the last successful refresh, if any.</param>
/// <param name="ProjectCount">Number of projects in the latest Processor configuration.</param>
public sealed record ProjectConfigStatus(string Source, DateTimeOffset? LastRefresh, int ProjectCount);
