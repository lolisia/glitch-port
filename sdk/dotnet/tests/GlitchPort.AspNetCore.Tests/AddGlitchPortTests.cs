using GlitchPort.AspNetCore.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GlitchPort.AspNetCore.Tests;

public sealed class AddGlitchPortTests
{
    [Fact]
    public void BindOptions_ReadsEverySupportedConfigurationKey()
    {
        using var temp = new TempDirectory();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(temp.Path, "configured-file"),
            ["GlitchPort:Release"] = "game@1.2.3",
            ["GlitchPort:Environment"] = "staging",
            ["GlitchPort:SampleRate"] = "0.25",
            ["GlitchPort:CaptureUnhandledExceptions"] = "false",
            ["GlitchPort:SendDefaultPii"] = "true",
            ["GlitchPort:MinimumBreadcrumbLevel"] = "Warning",
            ["GlitchPort:MinimumEventLevel"] = "Critical",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));
        services.AddGlitchPort(configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<GlitchPortClient>();
        var options = provider.GetRequiredService<GlitchPortOptions>();
        var integrationOptions = provider.GetRequiredService<GlitchPortAspNetCoreOptions>();

        Assert.Same(options, client.Options);
        Assert.Null(options.Dsn);
        Assert.Equal("configured-file", Assert.Single(options.Relays).Name);
        Assert.Equal("game@1.2.3", options.Release);
        Assert.Equal("staging", options.Environment);
        Assert.Equal(0.25d, options.SampleRate);
        Assert.False(options.CaptureUnhandledExceptions);
        Assert.True(options.SendDefaultPii);
        Assert.Equal(LogLevel.Warning, integrationOptions.MinimumBreadcrumbLevel);
        Assert.Equal(LogLevel.Critical, integrationOptions.MinimumEventLevel);
    }

    [Fact]
    public void BindOptions_AppliesDocumentedDefaults()
    {
        using var temp = new TempDirectory();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(temp.Path),
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Staging"));
        services.AddGlitchPort(configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<GlitchPortClient>();
        var options = provider.GetRequiredService<GlitchPortOptions>();
        var integrationOptions = provider.GetRequiredService<GlitchPortAspNetCoreOptions>();

        Assert.Equal("Staging", options.Environment);
        Assert.Null(options.Release);
        Assert.Equal(1d, options.SampleRate);
        Assert.True(options.CaptureUnhandledExceptions);
        Assert.False(options.SendDefaultPii);
        Assert.Equal(LogLevel.Information, integrationOptions.MinimumBreadcrumbLevel);
        Assert.Equal(LogLevel.Error, integrationOptions.MinimumEventLevel);
        Assert.Same(client.Options, options);
    }

    [Fact]
    public void ConfigureCallback_RunsAfterConfigurationBinding()
    {
        using var temp = new TempDirectory();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(temp.Path),
            ["GlitchPort:Release"] = "from-configuration",
            ["GlitchPort:Environment"] = "from-configuration",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));
        services.AddGlitchPort(configuration, options =>
        {
            options.Release = "from-configure";
            options.Environment = "from-configure";
            options.SampleRate = 0.5d;
            options.SdkName = "configured.sdk";
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<GlitchPortClient>().Options;

        Assert.Equal("from-configure", options.Release);
        Assert.Equal("from-configure", options.Environment);
        Assert.Equal(0.5d, options.SampleRate);
        Assert.Equal("configured.sdk", options.SdkName);
    }

    [Fact]
    public void ConfigureCallback_CanSupplyRelaysWhenNoDsnIsConfigured()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentVariable("GLITCHPORT_DSN", null);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));
        services.AddGlitchPort(BuildConfiguration(), options =>
            options.Relays.Add(Relay.File(relay => relay.Directory = temp.Path)));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<GlitchPortClient>();

        Assert.Single(client.Options.Relays);
        Assert.Equal("file", client.Relays[0].Type);
    }

    [Fact]
    public void AddGlitchPort_WithoutRelays_ThrowsClearInvalidOperationException()
    {
        using var environment = new EnvironmentVariable("GLITCHPORT_DSN", null);
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddGlitchPort(BuildConfiguration()));

        Assert.Contains("GlitchPort:Dsn", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GLITCHPORT_DSN", exception.Message, StringComparison.Ordinal);
        Assert.Contains("relay", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dsn_FallsBackToGlitchPortDsnEnvironmentVariable()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentVariable(
            "GLITCHPORT_DSN",
            TestRelay.CreateDsn(temp.Path, "environment-file"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));

        // A blank configuration value must not shadow the environment fallback.
        services.AddGlitchPort(BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = "   ",
        }));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<GlitchPortClient>().Options;

        Assert.Equal("environment-file", Assert.Single(options.Relays).Name);
    }

    [Fact]
    public void Dsn_AcceptsSemicolonSeparatedRelayList()
    {
        using var temp = new TempDirectory();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(temp.Path, "first")
                + ";"
                + TestRelay.CreateDsn(temp.Path, "second"),
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));
        services.AddGlitchPort(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<GlitchPortOptions>();

        Assert.Equal(
            new[] { "first", "second" },
            options.Relays.Select(relay => relay.Name).ToArray());
    }

    [Fact]
    public void Dsn_PrefersConfigurationOverEnvironmentVariable()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentVariable(
            "GLITCHPORT_DSN",
            TestRelay.CreateDsn(temp.Path, "environment-file"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));
        services.AddGlitchPort(BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(temp.Path, "configured-file"),
        }));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<GlitchPortClient>().Options;

        Assert.Equal("configured-file", Assert.Single(options.Relays).Name);
    }

    [Fact]
    public void AddGlitchPort_WithoutConfigurationArgument_ReadsRegisteredConfiguration()
    {
        using var temp = new TempDirectory();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(temp.Path, "registered-file"),
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));

        // No configuration argument: the extension method finds the registered
        // IConfiguration instance, like WebApplicationBuilder does.
        services.AddGlitchPort();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<GlitchPortOptions>();

        Assert.Equal("registered-file", Assert.Single(options.Relays).Name);
    }

    [Fact]
    public void InvalidDsn_ThrowsInvalidOperationExceptionNamingTheConfigurationKey()
    {
        using var environment = new EnvironmentVariable("GLITCHPORT_DSN", null);
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = "unknown:?dir=logs",
        });

        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddGlitchPort(configuration));

        Assert.Contains("GlitchPort:Dsn", exception.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Theory]
    [InlineData("SampleRate", "2", "GlitchPort:SampleRate")]
    [InlineData("SampleRate", "not-a-number", "GlitchPort:SampleRate")]
    [InlineData("CaptureUnhandledExceptions", "sometimes", "GlitchPort:CaptureUnhandledExceptions")]
    [InlineData("MinimumEventLevel", "Loud", "GlitchPort:MinimumEventLevel")]
    public void InvalidConfigurationValue_ThrowsClearInvalidOperationException(
        string key,
        string value,
        string expectedKeyInMessage)
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentVariable("GLITCHPORT_DSN", null);
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(temp.Path),
            ["GlitchPort:" + key] = value,
        });

        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddGlitchPort(configuration));

        Assert.Contains(expectedKeyInMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostShutdown_DisposesTheSingletonClient()
    {
        var factory = new GlitchPortApplicationFactory();
        var client = factory.Services.GetRequiredService<GlitchPortClient>();

        factory.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.CaptureMessageAsync("after host shutdown"));
    }

    [Fact]
    public async Task HostedService_DisposesClientAndFlushesEventsOnStop()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentVariable("GLITCHPORT_DSN", null);
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(temp.Path),
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));
        services.AddGlitchPort(configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<GlitchPortClient>();
        await client.CaptureMessageAsync("shutdown event", Level.Error);

        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.Contains(hostedServices, service => service.GetType().Name == "GlitchPortHostedService");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(hostedServices.Select(service => service.StopAsync(CancellationToken.None)));
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            "Stopping the host took " + stopwatch.Elapsed + ", which exceeds the bounded dispose flush.");

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.CaptureMessageAsync("after shutdown"));

        var events = TestRelay.Read(temp.Path);
        var captured = Assert.Single(events);
        Assert.Equal("shutdown event", captured["message"]?.GetValue<string>());
    }

    private static IConfiguration BuildConfiguration(IEnumerable<KeyValuePair<string, string?>>? values = null)
    {
        var builder = new ConfigurationBuilder();
        if (values is not null)
        {
            builder.AddInMemoryCollection(values);
        }

        return builder.Build();
    }
}
