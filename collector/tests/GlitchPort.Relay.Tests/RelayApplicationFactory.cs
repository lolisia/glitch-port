using System.Net;
using GlitchPort.Collector.Contracts;
using GlitchPort.Relay.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GlitchPort.Relay.Tests;

/// <summary>
/// Hosts the Relay with the test configuration, a replaceable envelope
/// publisher and a deterministic Processor project-config handler.
/// </summary>
/// <remarks>
/// Unless the test supplies its own handler, the factory answers every
/// project-config request with a successful snapshot for project
/// <see cref="RelayTestClient.ProjectId"/> and
/// <see cref="RelayTestClient.ValidKey"/>. That snapshot is loaded by the
/// immediate startup refresh; <see cref="WaitForProjectConfigLoadedAsync"/>
/// (and <see cref="CreateClient"/>) wait for it so ingest tests never race the
/// background service.
/// </remarks>
internal sealed class RelayApplicationFactory : WebApplicationFactory<Program>
{
    private const string SeededConfigVersion = "seed";

    private static readonly TimeSpan ProjectConfigLoadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProjectConfigPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly IEnvelopePublisher _publisher;
    private readonly FakeProjectConfigHandler _projectConfigHandler;
    private readonly IReadOnlyDictionary<string, string?>? _relayConfiguration;
    private readonly Action<IConfigurationBuilder>? _additionalConfiguration;
    private readonly bool _seedsProjectConfig;

    public RelayApplicationFactory(
        IEnvelopePublisher publisher,
        FakeProjectConfigHandler? projectConfigHandler = null,
        IReadOnlyDictionary<string, string?>? relayConfiguration = null,
        Action<IConfigurationBuilder>? additionalConfiguration = null)
    {
        _publisher = publisher;
        _seedsProjectConfig = projectConfigHandler is null;
        _projectConfigHandler = projectConfigHandler ?? CreateSeededProjectConfigHandler();
        _relayConfiguration = relayConfiguration;
        _additionalConfiguration = additionalConfiguration;
    }

    /// <summary>The fake handler receiving project-config refreshes.</summary>
    public FakeProjectConfigHandler ProjectConfigHandler => _projectConfigHandler;

    /// <summary>
    /// Creates a client. When the factory owns the seeded project-config
    /// response, waits until the immediate startup refresh loaded that snapshot
    /// so a test cannot race the background service.
    /// </summary>
    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();

        if (_seedsProjectConfig)
        {
            WaitForProjectConfigLoadedAsync().GetAwaiter().GetResult();
        }

        return client;
    }

    /// <summary>
    /// Creates a client and waits until the startup project-config refresh has
    /// loaded a snapshot, also when the test supplied its own handler.
    /// </summary>
    public async Task<HttpClient> CreateLoadedClientAsync()
    {
        var client = CreateClient();
        await WaitForProjectConfigLoadedAsync().ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Waits until <see cref="ProjectConfigProvider.Source"/> reports the
    /// <c>processor</c> source, with a bounded timeout and a short poll delay.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// The configuration was not loaded within <paramref name="timeout"/>.
    /// </exception>
    public async Task WaitForProjectConfigLoadedAsync(
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var provider = Services.GetRequiredService<ProjectConfigProvider>();
        var waitTimeout = timeout ?? ProjectConfigLoadTimeout;
        var delay = pollInterval ?? ProjectConfigPollInterval;
        var deadline = DateTimeOffset.UtcNow + waitTimeout;

        while (provider.Source != ProjectConfigProvider.ProcessorSource)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"The Relay project configuration was not loaded within "
                    + $"{waitTimeout.TotalSeconds:0.###}s (source: '{provider.Source}').");
            }

            await Task.Delay(delay).ConfigureAwait(false);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Relay:ProcessorEndpoints:0"] = "http://localhost:5090",
                ["Relay:InternalSharedSecret"] = "test-internal-secret",
                ["Relay:Timeout"] = "5",
            };

            if (_relayConfiguration is not null)
            {
                foreach (var (key, value) in _relayConfiguration)
                {
                    settings[key] = value;
                }
            }

            configuration.AddInMemoryCollection(settings);
            _additionalConfiguration?.Invoke(configuration);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEnvelopePublisher>();
            services.AddSingleton(_publisher);
            services.AddHttpClient(ProjectConfigClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => _projectConfigHandler);
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
        });
    }

    private static FakeProjectConfigHandler CreateSeededProjectConfigHandler() =>
        FakeProjectConfigHandler.ForConfig(
            SeededConfigVersion,
            (RelayTestClient.ProjectId, RelayTestClient.ValidKey, null));
}

/// <summary>
/// Gives the TestServer a deterministic client address. TestServer does not
/// assign one by default and the event protocol uses it for
/// <c>user.ip_address: "{{auto}}"</c> and <c>Envelope.ClientIp</c>.
/// </summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var header = context.Request.Headers[RelayTestClient.RemoteIpHeader].ToString();
                context.Connection.RemoteIpAddress = IPAddress.TryParse(header, out var address)
                    ? address
                    : IPAddress.Parse(RelayTestClient.DefaultRemoteIp);

                await nextMiddleware().ConfigureAwait(false);
            });

            next(app);
        };
    }
}
