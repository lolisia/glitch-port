using System.Net;
using GlitchPort.Collector.Contracts;
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
/// Hosts the Relay with the test configuration and a replaceable envelope
/// publisher.
/// </summary>
internal sealed class RelayApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IEnvelopePublisher _publisher;

    public RelayApplicationFactory(IEnvelopePublisher publisher)
    {
        _publisher = publisher;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:Projects:1:0"] = RelayTestClient.ValidKey,
                ["Relay:ProcessorEndpoints:0"] = "http://localhost:5090",
                ["Relay:InternalSharedSecret"] = "test-internal-secret",
                ["Relay:Timeout"] = "5",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEnvelopePublisher>();
            services.AddSingleton(_publisher);
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
