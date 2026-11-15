namespace GlitchPort.Relay.Config;

/// <summary>
/// Immutable snapshot of the project configuration served by the Processor.
/// Each project maps its active public keys to their optional per-key rate
/// limit. The snapshot also carries the Processor version used for conditional
/// refreshes.
/// </summary>
public sealed class ProjectConfigSnapshot
{
    /// <summary>The empty snapshot used before the first successful refresh.</summary>
    public static ProjectConfigSnapshot Empty { get; } = new(
        null,
        new Dictionary<long, IReadOnlyDictionary<string, int?>>());

    /// <summary>Creates a snapshot, copying the supplied project map.</summary>
    public ProjectConfigSnapshot(
        string? version,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, int?>> projects)
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
}
