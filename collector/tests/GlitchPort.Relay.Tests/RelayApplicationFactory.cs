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
internal sealed class RelayApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IEnvelopePublisher _publisher;
    private readonly FakeProjectConfigHandler _projectConfigHandler;
    private readonly IReadOnlyDictionary<string, string?>? _relayConfiguration;

    public RelayApplicationFactory(
        IEnvelopePublisher publisher,
        FakeProjectConfigHandler? projectConfigHandler = null,
        IReadOnlyDictionary<string, string?>? relayConfiguration = null)
    {
        _publisher = publisher;
        _projectConfigHandler = projectConfigHandler ?? new FakeProjectConfigHandler();
        _relayConfiguration = relayConfiguration;
    }

    /// <summary>The fake handler receiving project-config refreshes.</summary>
    public FakeProjectConfigHandler ProjectConfigHandler => _projectConfigHandler;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Relay:Projects:1:0"] = RelayTestClient.ValidKey,
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
