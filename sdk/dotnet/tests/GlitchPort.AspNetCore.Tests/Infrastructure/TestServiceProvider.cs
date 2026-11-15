using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GlitchPort.AspNetCore.Tests.Infrastructure;

/// <summary>
/// Builds a service provider that contains the glitch-port integration but no
/// web host, for configuration, logger and shutdown tests.
/// </summary>
internal static class TestServiceProvider
{
    public static ServiceProvider Create(
        string relayDirectory,
        IEnumerable<KeyValuePair<string, string?>>? settings = null,
        Action<GlitchPortOptions>? configure = null,
        string? environmentName = "Development")
    {
        ArgumentNullException.ThrowIfNull(relayDirectory);

        var configurationBuilder = new ConfigurationBuilder();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(relayDirectory),
        };

        if (settings is not null)
        {
            foreach (var setting in settings)
            {
                values[setting.Key] = setting.Value;
            }
        }

        configurationBuilder.AddInMemoryCollection(values);

        var services = new ServiceCollection();
        services.AddLogging();

        if (environmentName is not null)
        {
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
        }

        services.AddGlitchPort(configurationBuilder.Build(), configure);
        return services.BuildServiceProvider();
    }

    public static GlitchPortLoggerProvider GetLoggerProvider(this IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return provider.GetServices<ILoggerProvider>().OfType<GlitchPortLoggerProvider>().Single();
    }

    public static ILogger CreateLogger(this IServiceProvider provider, string category)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.GetLoggerProvider().CreateLogger(category);
    }
}

/// <summary>A minimal <see cref="IHostEnvironment"/> for tests that have no host.</summary>
internal sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string environmentName, string applicationName = "GlitchPort.AspNetCore.Tests")
    {
        EnvironmentName = environmentName;
        ApplicationName = applicationName;
    }

    public string EnvironmentName { get; set; }

    public string ApplicationName { get; set; }

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Restores an environment variable when it is disposed.</summary>
internal sealed class EnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _previousValue;

    public EnvironmentVariable(string name, string? value)
    {
        _name = name;
        _previousValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _previousValue);
    }
}
