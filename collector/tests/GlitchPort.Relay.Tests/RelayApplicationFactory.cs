using GlitchPort.Collector.Contracts;
using Microsoft.AspNetCore.Hosting;
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
                ["Relay:Projects:1:0"] = "test-key",
                ["Relay:ProcessorEndpoints:0"] = "http://localhost:5090",
                ["Relay:InternalSharedSecret"] = "test-internal-secret",
                ["Relay:Timeout"] = "5",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEnvelopePublisher>();
            services.AddSingleton(_publisher);
        });
    }
}
