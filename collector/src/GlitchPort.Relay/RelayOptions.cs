using System.Globalization;

namespace GlitchPort.Relay;

/// <summary>
/// Runtime configuration for the Relay, read from the <c>Relay</c>
/// configuration section.
/// </summary>
public sealed class RelayOptions
{
    /// <summary>Name of the configuration section that holds the Relay settings.</summary>
    public const string SectionName = "Relay";

    /// <summary>Timeout applied when <c>Relay:Timeout</c> is absent or invalid.</summary>
    public const double DefaultTimeoutSeconds = 10;

    /// <summary>
    /// Default interval between Processor project-config refreshes, in seconds.
    /// </summary>
    public const int DefaultConfigRefreshSeconds = 60;

    /// <summary>Smallest accepted project-config refresh interval, in seconds.</summary>
    public const int MinimumConfigRefreshSeconds = 1;

    /// <summary>
    /// Default lifetime of a negative cache entry for an unknown public key, in
    /// seconds.
    /// </summary>
    public const int DefaultUnknownKeyCacheSeconds = 30;

    /// <summary>
    /// Smallest accepted unknown-key cache lifetime, in seconds. Zero disables
    /// the negative cache.
    /// </summary>
    public const int MinimumUnknownKeyCacheSeconds = 0;

    /// <summary>
    /// Default ingest limit applied to a single project, in requests per
    /// minute.
    /// </summary>
    public const int DefaultRateLimitPerProjectPerMinute = 6000;

    /// <summary>
    /// Smallest accepted per-project rate limit. Zero disables the project
    /// limit.
    /// </summary>
    public const int MinimumRateLimitPerProjectPerMinute = 0;

    /// <summary>Base URLs of the Processor instances that receive envelopes.</summary>
    public string[] ProcessorEndpoints { get; init; } = [];

    /// <summary>Shared secret sent to the Processor in the internal header.</summary>
    public string InternalSharedSecret { get; init; } = string.Empty;

    /// <summary>
    /// Public ingest keys per project id. A request is authorized when its
    /// <c>X-GlitchPort-Key</c> header matches one of the project's keys.
    /// </summary>
    public IReadOnlyDictionary<long, string[]> Projects { get; init; } =
        new Dictionary<long, string[]>();

    /// <summary>Timeout applied to HTTP calls to the Processor.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <summary>
    /// Seconds between two Processor project-config refreshes. Values below
    /// <see cref="MinimumConfigRefreshSeconds"/> fall back to the default.
    /// </summary>
    public int ConfigRefreshSeconds { get; init; } = DefaultConfigRefreshSeconds;

    /// <summary>
    /// Seconds an unknown (project id, key) pair is negatively cached. Zero
    /// disables the negative cache; values below zero fall back to the default.
    /// </summary>
    public int UnknownKeyCacheSeconds { get; init; } = DefaultUnknownKeyCacheSeconds;

    /// <summary>
    /// Ingest limit applied to a single project, in requests per minute, read
    /// from <c>Relay:RateLimit:PerProjectPerMinute</c>. Zero means unlimited;
    /// negative or invalid values fall back to the default.
    /// </summary>
    public int RateLimitPerProjectPerMinute { get; init; } =
        DefaultRateLimitPerProjectPerMinute;

    /// <summary>
    /// Builds the options from <paramref name="configuration"/>, falling back to
    /// safe defaults when values are missing or invalid.
    /// </summary>
    public static RelayOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        return new RelayOptions
        {
            ProcessorEndpoints = section.GetSection("ProcessorEndpoints").Get<string[]>() ?? [],
            InternalSharedSecret = section["InternalSharedSecret"] ?? string.Empty,
            Projects = section.GetSection("Projects").Get<Dictionary<long, string[]>>()
                ?? new Dictionary<long, string[]>(),
            Timeout = TimeSpan.FromSeconds(ReadTimeoutSeconds(section)),
            ConfigRefreshSeconds = ReadSeconds(
                section,
                "ConfigRefreshSeconds",
                DefaultConfigRefreshSeconds,
                MinimumConfigRefreshSeconds),
            UnknownKeyCacheSeconds = ReadSeconds(
                section,
                "UnknownKeyCacheSeconds",
                DefaultUnknownKeyCacheSeconds,
                MinimumUnknownKeyCacheSeconds),
            RateLimitPerProjectPerMinute = ReadRateLimitPerProjectPerMinute(section),
        };
    }

    private static int ReadRateLimitPerProjectPerMinute(IConfigurationSection section)
    {
        var rawValue = section.GetSection("RateLimit")["PerProjectPerMinute"];
        if (int.TryParse(
                rawValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var requestsPerMinute)
            && requestsPerMinute >= MinimumRateLimitPerProjectPerMinute)
        {
            return requestsPerMinute;
        }

        return DefaultRateLimitPerProjectPerMinute;
    }

    private static int ReadSeconds(
        IConfigurationSection section,
        string key,
        int defaultValue,
        int minimumValue)
    {
        var rawValue = section[key];
        if (int.TryParse(
                rawValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var seconds)
            && seconds >= minimumValue)
        {
            return seconds;
        }

        return defaultValue;
    }

    private static double ReadTimeoutSeconds(IConfigurationSection section)
    {
        var rawValue = section["Timeout"];
        if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return seconds;
        }

        return DefaultTimeoutSeconds;
    }
}
