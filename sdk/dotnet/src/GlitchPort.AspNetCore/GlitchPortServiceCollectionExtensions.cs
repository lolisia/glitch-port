using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GlitchPort.AspNetCore;

/// <summary>
/// Registers the glitch-port client, the request middleware dependencies and the
/// <see cref="ILoggerProvider"/> that forwards log entries to glitch-port.
/// </summary>
public static class GlitchPortServiceCollectionExtensions
{
    /// <summary>
    /// Adds the glitch-port client and its ASP.NET Core integration to the
    /// service collection.
    /// </summary>
    /// <param name="services">The service collection to update.</param>
    /// <param name="config">
    /// The application configuration. When <see langword="null"/> the method
    /// looks for an <see cref="IConfiguration"/> instance registered in
    /// <paramref name="services"/> (the host registers one). The <c>GlitchPort</c>
    /// section is bound as follows:
    /// <list type="table">
    /// <item><term><c>Dsn</c></term><description>
    /// A semicolon separated relay list using the same syntax as
    /// <c>GLITCHPORT_DSN</c>. The environment variable is used when the key is
    /// absent or blank.
    /// </description></item>
    /// <item><term><c>Release</c></term><description>The release identifier.</description></item>
    /// <item><term><c>Environment</c></term><description>
    /// The deployment environment. Falls back to
    /// <see cref="IHostEnvironment.EnvironmentName"/>.
    /// </description></item>
    /// <item><term><c>SampleRate</c></term><description>A number between 0 and 1.</description></item>
    /// <item><term><c>CaptureUnhandledExceptions</c></term><description>
    /// Whether process-wide unhandled exceptions are captured. Defaults to
    /// <see langword="true"/> for ASP.NET Core hosts.
    /// </description></item>
    /// <item><term><c>SendDefaultPii</c></term><description>
    /// Whether the middleware may record the authenticated user name and the
    /// client IP address. Optional; defaults to <see langword="false"/>.
    /// </description></item>
    /// <item><term><c>MinimumBreadcrumbLevel</c></term><description>
    /// The minimum log level recorded as a breadcrumb. Defaults to
    /// <see cref="LogLevel.Information"/>.
    /// </description></item>
    /// <item><term><c>MinimumEventLevel</c></term><description>
    /// The minimum log level that creates an event. Defaults to
    /// <see cref="LogLevel.Error"/>.
    /// </description></item>
    /// </list>
    /// </param>
    /// <param name="configure">
    /// An optional callback applied after the configuration section, so it can
    /// override every bound value or add relays. When the callback leaves
    /// <see cref="GlitchPortOptions.Environment"/> empty, the host environment
    /// name is used as the fallback.
    /// </param>
    /// <returns><paramref name="services"/> so registrations can be chained.</returns>
    /// <exception cref="InvalidOperationException">
    /// Neither <c>GlitchPort:Dsn</c>, <c>GLITCHPORT_DSN</c> nor
    /// <paramref name="configure"/> yields at least one relay, or a configured
    /// value is invalid.
    /// </exception>
    public static IServiceCollection AddGlitchPort(
        this IServiceCollection services,
        IConfiguration? config = null,
        Action<GlitchPortOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configuration = config ?? FindConfiguration(services);
        var integrationOptions = new GlitchPortAspNetCoreOptions();
        var options = GlitchPortConfiguration.BindOptions(configuration, integrationOptions);

        // The callback runs last: it can override any bound value and, because
        // relays are just definitions, it can also supply the relay list.
        configure?.Invoke(options);

        if (!GlitchPortConfiguration.HasRelay(options))
        {
            throw new InvalidOperationException(
                "GlitchPort is not configured: set 'GlitchPort:Dsn' or the GLITCHPORT_DSN environment variable "
                + "to at least one relay (for example 'http://key@localhost:5080/1' or "
                + "'file:?dir=logs/glitchport'), or add a relay to GlitchPortOptions.Relays in the "
                + "AddGlitchPort configure callback.");
        }

        services.AddSingleton(integrationOptions);

        // The host environment is only available from the provider, so the
        // documented "Environment falls back to IHostEnvironment.EnvironmentName"
        // rule is applied the first time the options are resolved.
        services.AddSingleton<GlitchPortOptions>(provider => ApplyHostEnvironment(provider, options));
        services.AddSingleton<GlitchPortClient>(
            provider => new GlitchPortClient(provider.GetRequiredService<GlitchPortOptions>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, GlitchPortLoggerProvider>());

        // The host's global logging filter would otherwise drop Trace/Debug
        // entries before the provider sees them, making the documented
        // minimum levels unenforceable. The provider's own IsEnabled applies the
        // configured thresholds instead.
        services.AddLogging(logging => logging.AddFilter<GlitchPortLoggerProvider>(null, LogLevel.Trace));

        services.AddHostedService<GlitchPortHostedService>();

        return services;
    }

    private static GlitchPortOptions ApplyHostEnvironment(IServiceProvider provider, GlitchPortOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Environment))
        {
            return options;
        }

        var hostEnvironment = provider.GetService<IHostEnvironment>();
        if (!string.IsNullOrWhiteSpace(hostEnvironment?.EnvironmentName))
        {
            options.Environment = hostEnvironment.EnvironmentName;
        }

        return options;
    }

    private static IConfiguration? FindConfiguration(IServiceCollection services)
    {
        // The generic host and WebApplicationBuilder register the configuration
        // as a singleton instance before user code runs.
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ImplementationInstance is IConfiguration configuration
                && typeof(IConfiguration).IsAssignableFrom(descriptor.ServiceType))
            {
                return configuration;
            }
        }

        return null;
    }
}

/// <summary>
/// Stops the glitch-port client when the host shuts down. Disposing the client
/// flushes the relays for at most two seconds and never throws.
/// </summary>
internal sealed class GlitchPortHostedService : IHostedService
{
    private readonly GlitchPortClient _client;

    public GlitchPortHostedService(GlitchPortClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _client.Dispose();
        return Task.CompletedTask;
    }
}
