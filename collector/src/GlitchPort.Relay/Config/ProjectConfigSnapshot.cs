using GlitchPort.Relay.Filtering;

namespace GlitchPort.Relay.Config;

/// <summary>
/// Immutable snapshot of the project configuration served by the Processor.
/// Each project maps its active public keys to their optional per-key rate
/// limit and carries its compiled inbound filters. The snapshot also carries
/// the Processor version used for conditional refreshes.
/// </summary>
public sealed class ProjectConfigSnapshot
{
    private static readonly IReadOnlyDictionary<long, InboundFilter> NoFilters =
        new Dictionary<long, InboundFilter>();

    /// <summary>The empty snapshot used before the first successful refresh.</summary>
    public static ProjectConfigSnapshot Empty { get; } = new(
        null,
        new Dictionary<long, IReadOnlyDictionary<string, int?>>());

    private readonly IReadOnlyDictionary<long, InboundFilter> _inboundFilters;

    /// <summary>Creates a snapshot without inbound filters.</summary>
    public ProjectConfigSnapshot(
        string? version,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, int?>> projects)
        : this(version, projects, inboundFilters: null)
    {
    }

    /// <summary>
    /// Creates a snapshot, copying the supplied project map and compiling the
    /// inbound filter globs of each project exactly once.
    /// </summary>
    public ProjectConfigSnapshot(
        string? version,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, int?>> projects,
        IReadOnlyDictionary<long, InboundFilterConfig>? inboundFilters)
    {
        ArgumentNullException.ThrowIfNull(projects);

        Version = version;

        var copy = new Dictionary<long, IReadOnlyDictionary<string, int?>>(projects.Count);
        foreach (var (projectId, keys) in projects)
        {
            ArgumentNullException.ThrowIfNull(keys);
            copy[projectId] = new Dictionary<string, int?>(keys, StringComparer.Ordinal);
        }

        Projects = copy;

        var filters = new Dictionary<long, InboundFilter>(inboundFilters?.Count ?? 0);
        if (inboundFilters is not null)
        {
            foreach (var (projectId, config) in inboundFilters)
            {
                ArgumentNullException.ThrowIfNull(config);
                filters[projectId] = new InboundFilter(
                    config.Releases,
                    config.Environments,
                    config.MessagePatterns);
            }
        }

        _inboundFilters = filters.Count == 0 ? NoFilters : filters;
    }

    /// <summary>Opaque Processor configuration version, if one is known.</summary>
    public string? Version { get; }

    /// <summary>Public keys per project id.</summary>
    public IReadOnlyDictionary<long, IReadOnlyDictionary<string, int?>> Projects { get; }

    /// <summary>Number of projects present in this snapshot.</summary>
    public int ProjectCount => Projects.Count;

    /// <summary>
    /// Looks up the optional rate limit of <paramref name="publicKey"/> in
    /// <paramref name="projectId"/>.
    /// </summary>
    public bool TryGetRateLimit(long projectId, string publicKey, out int? rateLimitPerMinute)
    {
        rateLimitPerMinute = null;

        return !string.IsNullOrEmpty(publicKey)
            && Projects.TryGetValue(projectId, out var keys)
            && keys.TryGetValue(publicKey, out rateLimitPerMinute);
    }

    /// <summary>
    /// Looks up the compiled inbound filters of <paramref name="projectId"/>.
    /// Returns <see langword="false"/> when the project has no filter (for
    /// example because the Processor is older and sends no
    /// <c>inbound_filters</c> field), in which case nothing must be filtered.
    /// </summary>
    public bool TryGetInboundFilters(long projectId, out InboundFilter filters)
    {
        if (_inboundFilters.TryGetValue(projectId, out var found) && found.HasFilters)
        {
            filters = found;
            return true;
        }

        filters = InboundFilter.Empty;
        return false;
    }
}
