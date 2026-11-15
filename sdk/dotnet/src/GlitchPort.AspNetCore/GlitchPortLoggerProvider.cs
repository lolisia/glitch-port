using Microsoft.Extensions.Logging;

namespace GlitchPort.AspNetCore;

/// <summary>
/// Forwards <see cref="ILogger"/> entries to glitch-port: entries at or above
/// <see cref="GlitchPortAspNetCoreOptions.MinimumEventLevel"/> become events and
/// entries at or above <see cref="GlitchPortAspNetCoreOptions.MinimumBreadcrumbLevel"/>
/// become breadcrumbs on the current scope.
/// </summary>
/// <remarks>
/// Categories starting with <c>GlitchPort</c> are ignored so the SDK can never
/// report its own diagnostics recursively. When a log entry carries an exception
/// that the request middleware (or another logger call) already captured, the
/// entry is skipped: an exception instance is reported at most once.
/// </remarks>
public sealed class GlitchPortLoggerProvider : ILoggerProvider
{
    private const string IgnoredCategoryPrefix = "GlitchPort";

    private readonly GlitchPortClient _client;
    private readonly GlitchPortAspNetCoreOptions _options;

    /// <summary>Creates a provider that forwards entries to <paramref name="client"/>.</summary>
    /// <param name="client">The client that captures events and breadcrumbs.</param>
    /// <param name="options">The log level thresholds.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public GlitchPortLoggerProvider(GlitchPortClient client, GlitchPortAspNetCoreOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);

        if (categoryName.StartsWith(IgnoredCategoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return NullLogger.Instance;
        }

        return new GlitchPortLogger(categoryName, _client, _options);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // The client is a singleton owned by the host; the provider must not
        // dispose it because other services (and the hosted service) still use it.
    }

    /// <summary>The logger returned for categories that are intentionally ignored.</summary>
    private sealed class NullLogger : ILogger
    {
        internal static readonly NullLogger Instance = new NullLogger();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return false;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Ignored categories are intentionally dropped.
        }
    }
}
