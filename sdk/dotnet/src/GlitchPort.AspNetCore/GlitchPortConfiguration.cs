using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GlitchPort.AspNetCore;

/// <summary>
/// Reads the <c>GlitchPort</c> configuration section into the client and
/// integration options.
/// </summary>
internal static class GlitchPortConfiguration
{
    /// <summary>The configuration section that holds every integration setting.</summary>
    internal const string SectionName = "GlitchPort";

    /// <summary>The environment variable that holds the fallback relay list.</summary>
    internal const string DsnEnvironmentVariable = "GLITCHPORT_DSN";

    internal const string DsnKey = "Dsn";
    internal const string ReleaseKey = "Release";
    internal const string EnvironmentKey = "Environment";
    internal const string SampleRateKey = "SampleRate";
    internal const string CaptureUnhandledExceptionsKey = "CaptureUnhandledExceptions";
    internal const string SendDefaultPiiKey = "SendDefaultPii";
    internal const string MinimumBreadcrumbLevelKey = "MinimumBreadcrumbLevel";
    internal const string MinimumEventLevelKey = "MinimumEventLevel";

    /// <summary>
    /// Binds the <c>GlitchPort</c> section. The configure callback is applied by
    /// the caller so it can override every value read here.
    /// </summary>
    /// <param name="configuration">The application configuration, or <see langword="null"/>.</param>
    /// <param name="integrationOptions">The integration options to fill in.</param>
    /// <returns>The client options read from configuration and the environment.</returns>
    /// <exception cref="InvalidOperationException">A configured value is invalid.</exception>
    internal static GlitchPortOptions BindOptions(
        IConfiguration? configuration,
        GlitchPortAspNetCoreOptions integrationOptions)
    {
        ArgumentNullException.ThrowIfNull(integrationOptions);

        var section = configuration?.GetSection(SectionName);
        var options = new GlitchPortOptions();

        var dsn = GetString(section, DsnKey);
        if (dsn is not null)
        {
            AddRelays(options, dsn, "GlitchPort:Dsn");
        }
        else
        {
            // The environment variable is the documented fallback and uses the
            // exact same semicolon separated relay list syntax.
            var environmentDsn = Environment.GetEnvironmentVariable(DsnEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentDsn))
            {
                AddRelays(options, environmentDsn.Trim(), DsnEnvironmentVariable);
            }
        }

        var release = GetString(section, ReleaseKey);
        if (release is not null)
        {
            options.Release = release;
        }

        var environment = GetString(section, EnvironmentKey);
        if (environment is not null)
        {
            options.Environment = environment;
        }

        var sampleRate = GetString(section, SampleRateKey);
        if (sampleRate is not null)
        {
            if (!double.TryParse(
                    sampleRate,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsedSampleRate)
                || double.IsNaN(parsedSampleRate)
                || parsedSampleRate < 0d
                || parsedSampleRate > 1d)
            {
                throw new InvalidOperationException(
                    "GlitchPort:SampleRate must be a number between 0 and 1, but the configured value was '"
                    + sampleRate
                    + "'.");
            }

            options.SampleRate = parsedSampleRate;
        }

        // ASP.NET Core captures unhandled exceptions by default, unlike the
        // dependency-free core library.
        options.CaptureUnhandledExceptions = true;
        var captureUnhandled = GetString(section, CaptureUnhandledExceptionsKey);
        if (captureUnhandled is not null)
        {
            options.CaptureUnhandledExceptions = ParseBoolean(captureUnhandled, CaptureUnhandledExceptionsKey);
        }

        var sendDefaultPii = GetString(section, SendDefaultPiiKey);
        if (sendDefaultPii is not null)
        {
            options.SendDefaultPii = ParseBoolean(sendDefaultPii, SendDefaultPiiKey);
        }

        integrationOptions.MinimumBreadcrumbLevel = ParseLogLevel(
            GetString(section, MinimumBreadcrumbLevelKey),
            MinimumBreadcrumbLevelKey,
            LogLevel.Information);

        integrationOptions.MinimumEventLevel = ParseLogLevel(
            GetString(section, MinimumEventLevelKey),
            MinimumEventLevelKey,
            LogLevel.Error);

        return options;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the options describe at least one
    /// relay the client can create.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <returns><see langword="true"/> when a relay is configured.</returns>
    internal static bool HasRelay(GlitchPortOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Dsn is not null)
        {
            return true;
        }

        for (var i = 0; i < options.Relays.Count; i++)
        {
            if (options.Relays[i] is { Enabled: true })
            {
                return true;
            }
        }

        return false;
    }

    private static void AddRelays(GlitchPortOptions options, string value, string source)
    {
        IReadOnlyList<RelayDefinition> relays;
        try
        {
            relays = RelayConfigParser.Parse(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                source + " contains an invalid relay configuration: " + ex.Message,
                ex);
        }

        for (var i = 0; i < relays.Count; i++)
        {
            options.Relays.Add(relays[i]);
        }
    }

    private static string? GetString(IConfigurationSection? section, string key)
    {
        var value = section?[key];
        if (value is null)
        {
            return null;
        }

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    private static bool ParseBoolean(string value, string key)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            "GlitchPort:" + key + " must be 'true' or 'false', but the configured value was '" + value + "'.");
    }

    private static LogLevel ParseLogLevel(string? value, string key, LogLevel defaultValue)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level)
            && Enum.IsDefined(typeof(LogLevel), level))
        {
            return level;
        }

        throw new InvalidOperationException(
            "GlitchPort:"
            + key
            + " must be one of Trace, Debug, Information, Warning, Error, Critical or None, but the configured value was '"
            + value
            + "'.");
    }
}
