using System.Net;
using System.Text;
using GlitchPort.Relay.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GlitchPort.Relay.Tests;

/// <summary>
/// Proves that the Processor project-config snapshot is the only key source:
/// missing configuration (503), unknown keys (401), revoked keys (401) and a
/// legacy configuration section that must be ignored.
/// </summary>
public sealed class ProjectConfigSourceTests
{
    private const string MessageJson = """
        { "message": "hello from the project config source test" }
        """;

    [Fact]
    public async Task Ingest_BeforeFirstConfigurationLoad_Returns503WithoutPublishingOrCachingTheKey()
    {
        var publisher = new RecordingEnvelopePublisher();

        // This handler fails every refresh, so the Relay never loads a
        // snapshot and keeps reporting the "none" source.
        using var factory = new RelayApplicationFactory(
            publisher,
            new FakeProjectConfigHandler(),
            relayConfiguration: new Dictionary<string, string?>
            {
                // A limit of one would answer the second request of each
                // endpoint with 429 if the rate limiter ran first.
                ["Relay:RateLimit:PerProjectPerMinute"] = "1",
            });
        using var client = factory.CreateClient();
        var provider = factory.Services.GetRequiredService<ProjectConfigProvider>();

        await factory.ProjectConfigHandler.WaitForRequestCountAsync(1);
        Assert.Equal("none", provider.Source);

        foreach (var path in new[]
                 {
                     RelayTestClient.EventsPath(),
                     RelayTestClient.MessagesPath(),
                 })
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // The unsupported content encoding proves the 503 happens
                // before the body is read: reading it would answer 400.
                using var response = await RelayTestClient.PostJsonAsync(
                    client,
                    path,
                    MessageJson,
                    RelayTestClient.ValidKey,
                    contentEncoding: "br");

                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
                Assert.Equal(
                    """{"error":"project configuration not loaded"}""",
                    await response.Content.ReadAsStringAsync());
            }
        }

        Assert.Empty(publisher.Envelopes);
        Assert.Equal(0, provider.UnknownKeyCacheCount);
        Assert.Equal(0, provider.UnknownKeyCacheHits);
    }

    [Fact]
    public async Task Ingest_WithLoadedSnapshotAndUnknownKey_ReturnsUnauthorized()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            "not-in-the-processor-snapshot");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task Ingest_AfterRefresh_RejectsKeyRemovedFromTheSnapshot()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using (var accepted = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            RelayTestClient.ValidKey))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        // The next snapshot keeps the project but only the replacement key.
        factory.ProjectConfigHandler.EnqueueConfig(
            "v2",
            (RelayTestClient.ProjectId, "replacement-key", null));
        var cache = factory.Services.GetRequiredService<ProjectConfigCache>();
        Assert.True(await cache.RefreshOnceAsync(CancellationToken.None));

        using var rejected = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            RelayTestClient.ValidKey);

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Single(publisher.Envelopes);

        using var replacement = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            "replacement-key");

        Assert.Equal(HttpStatusCode.Accepted, replacement.StatusCode);
    }

    [Fact]
    public async Task LegacyStaticConfigurationSection_IsIgnored()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(
            publisher,
            additionalConfiguration: configuration => configuration.AddJsonStream(
                new MemoryStream(
                    Encoding.UTF8.GetBytes(
                        """{"Relay":{"Projects":{"1":["legacy-key"]}}}"""))));
        using var client = await factory.CreateLoadedClientAsync();

        // The legacy document really is part of the configuration; the Relay
        // must still authorize only against the Processor snapshot.
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.True(configuration.GetSection("Relay").GetSection("Projects").Exists());

        using (var legacy = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            "legacy-key"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
        }

        using (var processorKey = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            RelayTestClient.ValidKey))
        {
            Assert.Equal(HttpStatusCode.Accepted, processorKey.StatusCode);
        }

        Assert.Single(publisher.Envelopes);
    }
}
