using Microsoft.Extensions.Logging;

namespace GlitchPort.AspNetCore;

/// <summary>
/// One category's logger in the glitch-port forwarding pipeline.
/// </summary>
internal sealed class GlitchPortLogger : ILogger
{
    private readonly string _category;
    private readonly GlitchPortClient _client;
    private readonly GlitchPortAspNetCoreOptions _options;

    public GlitchPortLogger(string category, GlitchPortClient client, GlitchPortAspNetCoreOptions options)
    {
        _category = category;
        _client = client;
        _options = options;
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        // glitch-port does not expose logging scopes; breadcrumbs are attached
        // to the client scope pushed by the request middleware instead.
        return null;
    }

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
        {
            return false;
        }

        return logLevel >= _options.MinimumEventLevel || logLevel >= _options.MinimumBreadcrumbLevel;
    }

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (logLevel == LogLevel.None || !IsEnabled(logLevel))
        {
            return;
        }

        var isEvent = logLevel >= _options.MinimumEventLevel;

        // Check-and-mark before building the event. TryMark is atomic, so two
        // concurrent log calls for the same exception instance report it once.
        if (isEvent && exception is not null && !GlitchPortExceptionMarker.TryMark(exception))
        {
            return;
        }

        var message = FormatMessage(formatter, state, exception);

        if (isEvent)
        {
            // The protocol requires a message or an exception; a state whose
            // formatter produced nothing still identifies its category.
            CaptureEvent(logLevel, message.Length == 0 ? _category : message, exception);
            return;
        }

        var breadcrumb = new Breadcrumb
        {
            Timestamp = DateTimeOffset.UtcNow,
            Category = _category,
            Message = message,
            Level = MapLevel(logLevel),
            Type = "log",
        };

        try
        {
            // Adds to the request scope pushed by UseGlitchPort, or to the global
            // scope when the application logs outside a request.
            _client.AddBreadcrumb(breadcrumb);
        }
        catch (Exception)
        {
            // Logging must never throw into the application.
        }
    }

    private void CaptureEvent(LogLevel logLevel, string message, Exception? exception)
    {
        var capturedEvent = new GlitchPortEvent
        {
            Level = MapLevel(logLevel),
            Logger = _category,
            Message = message,
        };

        if (exception is not null)
        {
            try
            {
                capturedEvent.Exceptions = ExceptionParser.Parse(exception, _client.Options);
            }
            catch (Exception)
            {
                // A malformed exception must not lose the event; the formatted
                // message stays as the only payload.
            }
        }

        try
        {
            var capture = _client.CaptureEventAsync(capturedEvent);
            if (capture.IsFaulted)
            {
                // Observe the fault (for example during shutdown) without
                // surfacing it to the application or the task scheduler.
                _ = capture.Exception;
            }
        }
        catch (Exception)
        {
            // Capture enqueues synchronously and never throws in normal use;
            // keep a misbehaving client (or a disposed one) away from the app.
        }
    }

    private static string FormatMessage<TState>(
        Func<TState, Exception?, string> formatter,
        TState state,
        Exception? exception)
    {
        try
        {
            return formatter(state, exception) ?? string.Empty;
        }
        catch (Exception)
        {
            try
            {
                return state is null ? string.Empty : state.ToString() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    private static Level MapLevel(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => Level.Debug,
            LogLevel.Debug => Level.Debug,
            LogLevel.Information => Level.Info,
            LogLevel.Warning => Level.Warning,
            LogLevel.Error => Level.Error,
            LogLevel.Critical => Level.Fatal,
            _ => Level.Fatal,
        };
    }
}
