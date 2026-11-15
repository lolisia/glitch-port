namespace GlitchPort.Relay.Config;

/// <summary>
/// Merges the static <c>Relay:Projects</c> configuration with the latest
/// Processor project configuration and authorizes public keys against the
/// union. Unknown (project id, key) pairs are negatively cached for
/// <c>Relay:UnknownKeyCacheSeconds</c>; a successful Processor refresh clears
/// that cache so keys created in the meantime start working immediately.
/// </summary>
/// <remarks>
/// The negative cache is a best-effort optimization that only avoids repeating
/// lookups for pairs that are known to be unknown. Unknown keys arrive straight
/// from a public request header, so they are attacker controlled: the cache
/// never stores keys longer than <see cref="MaxUnknownKeyCacheKeyLength"/>
/// characters, never grows beyond <see cref="MaxUnknownKeyCacheEntries"/>
/// entries and is never scanned per request. Authentication itself does not
/// depend on the cache.
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

    private const string StaticSource = "static";
    private const string ProcessorSource = "processor";

    private readonly object _gate = new();
    private readonly IReadOnlyDictionary<long, string[]> _staticProjects;
    private readonly TimeSpan _unknownKeyCacheDuration;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<(long ProjectId, string Key), DateTimeOffset> _unknownKeys = [];

    private ProjectConfigSnapshot _snapshot = ProjectConfigSnapshot.Empty;
    private DateTimeOffset? _lastRefresh;
    private bool _refreshedFromProcessor;
    private long _unknownKeyCacheHits;

    /// <summary>Creates a provider from the Relay options.</summary>
    /// <param name="options">Static Relay configuration.</param>
    /// <param name="timeProvider">Clock used for negative-cache expiry.</param>
    public ProjectConfigProvider(RelayOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _staticProjects = options.Projects;
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

    /// <summary>Where the active project configuration came from.</summary>
    public string Source
    {
        get
        {
            lock (_gate)
            {
                return _refreshedFromProcessor ? ProcessorSource : StaticSource;
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

    /// <summary>Union of the static and cached project ids.</summary>
    public int ProjectCount
    {
        get
        {
            lock (_gate)
            {
                return CountProjects();
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
    /// cache instead of the static or Processor configuration.
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
                _refreshedFromProcessor ? ProcessorSource : StaticSource,
                _lastRefresh,
                CountProjects());
        }
    }

    /// <summary>
    /// Authorizes <paramref name="publicKey"/> for <paramref name="projectId"/>
    /// against the static and cached configuration.
    /// </summary>
    public PublicKeyAuthorization? Authorize(long projectId, string? publicKey)
    {
        if (string.IsNullOrEmpty(publicKey))
        {
            return null;
        }

        lock (_gate)
        {
            var isStaticKey = _staticProjects.TryGetValue(projectId, out var staticKeys)
                && staticKeys is not null
                && staticKeys.Contains(publicKey, StringComparer.Ordinal);

            var now = _timeProvider.GetUtcNow();
            if (_unknownKeyCacheDuration > TimeSpan.Zero
                && _unknownKeys.TryGetValue((projectId, publicKey), out var expiresAt))
            {
                if (expiresAt > now)
                {
                    // The static configuration is immutable for the lifetime of
                    // the Relay, so an accepted static key can never have been
                    // cached as unknown; let it through to pick up a Processor
                    // rate limit when the cached snapshot also knows the key.
                    if (!isStaticKey)
                    {
                        _unknownKeyCacheHits++;
                        return null;
                    }
                }
                else
                {
                    _unknownKeys.Remove((projectId, publicKey));
                }
            }

            int? cachedRateLimit = null;
            var isCachedKey = _snapshot.TryGetRateLimit(projectId, publicKey, out cachedRateLimit);

            if (isStaticKey || isCachedKey)
            {
                return new PublicKeyAuthorization(
                    projectId,
                    publicKey,
                    isCachedKey ? cachedRateLimit : null);
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
            _refreshedFromProcessor = true;
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
            _refreshedFromProcessor = true;
            _unknownKeys.Clear();
        }
    }

    private int CountProjects()
    {
        var projectIds = new HashSet<long>(_staticProjects.Keys);

        foreach (var projectId in _snapshot.Projects.Keys)
        {
            projectIds.Add(projectId);
        }

        return projectIds.Count;
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
/// <param name="Source">Either <c>static</c> or <c>processor</c>.</param>
/// <param name="LastRefresh">Time of the last successful refresh, if any.</param>
/// <param name="ProjectCount">Union of static and cached project ids.</param>
public sealed record ProjectConfigStatus(string Source, DateTimeOffset? LastRefresh, int ProjectCount);
