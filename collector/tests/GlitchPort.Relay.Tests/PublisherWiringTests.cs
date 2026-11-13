using GlitchPort.Collector.Contracts;
using GlitchPort.Transport.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class PublisherWiringTests
{
    [Fact]
    public void DefaultPublisher_IsHttpEnvelopePublisher()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

        var publisher = factory.Services.GetRequiredService<IEnvelopePublisher>();

        Assert.IsType<HttpEnvelopePublisher>(publisher);
    }

    [Fact]
    public void HttpClient_UsesConfiguredTimeout()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());

        var httpClient = factory.Services.GetRequiredService<HttpClient>();

        Assert.Equal(TimeSpan.FromSeconds(5), httpClient.Timeout);
    }
}
