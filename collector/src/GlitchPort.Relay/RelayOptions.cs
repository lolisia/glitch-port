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
        };
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
