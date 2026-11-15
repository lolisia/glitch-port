using System.Net;
using System.Text.Json;
using GlitchPort.Relay.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task GetHealthz_WithoutSuccessfulRefresh_ReportsStaticConfig()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());
        using var client = factory.CreateClient();

        // Proves the dedicated project-config client uses the injected fake
        // handler and that the cache refreshes immediately on startup.
        await factory.ProjectConfigHandler.WaitForRequestCountAsync(1);

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await RelayTestClient.ReadJsonAsync(response);
        Assert.Equal("ok", body.GetProperty("status").GetString());

        var config = body.GetProperty("config");
        Assert.Equal("static", config.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("last_refresh").ValueKind);
        Assert.Equal(1, config.GetProperty("project_count").GetInt32());
    }

    [Fact]
    public async Task GetHealthz_AfterSuccessfulRefresh_ReportsProcessorAndUnionProjectCount()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());
        using var client = factory.CreateClient();
        var provider = factory.Services.GetRequiredService<ProjectConfigProvider>();
        var refreshedAt = new DateTimeOffset(2026, 11, 15, 7, 30, 0, TimeSpan.Zero);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot(
                "v1",
                (1, "processor-key", null),
                (9, "other-project-key", null)),
            refreshedAt);

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await RelayTestClient.ReadJsonAsync(response);
        var config = body.GetProperty("config");
        Assert.Equal("processor", config.GetProperty("source").GetString());
        Assert.Equal(
            "2026-11-15T07:30:00.0000000Z",
            config.GetProperty("last_refresh").GetString());
        Assert.Equal(2, config.GetProperty("project_count").GetInt32());
    }

    [Fact]
    public async Task GetHealthz_AfterFailedRefresh_KeepsProcessorSourceAndLastRefresh()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());
        using var client = factory.CreateClient();
        var provider = factory.Services.GetRequiredService<ProjectConfigProvider>();
        var options = factory.Services.GetRequiredService<RelayOptions>();
        var refreshedAt = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, "processor-key", null)),
            refreshedAt);

        using var httpClient = new HttpClient(new FakeProjectConfigHandler());
        using var cache = new ProjectConfigCache(
            new ProjectConfigClient(httpClient, NullLogger<ProjectConfigClient>.Instance),
            provider,
            options,
            NullLogger<ProjectConfigCache>.Instance);

        Assert.False(await cache.RefreshOnceAsync(CancellationToken.None));

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await RelayTestClient.ReadJsonAsync(response);
        var config = body.GetProperty("config");
        Assert.Equal("processor", config.GetProperty("source").GetString());
        Assert.Equal(
            "2026-11-15T08:00:00.0000000Z",
            config.GetProperty("last_refresh").GetString());
        Assert.Equal(1, config.GetProperty("project_count").GetInt32());
    }
}
