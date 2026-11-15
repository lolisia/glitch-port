using System.Net;
using GlitchPort.Relay.Config;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class ProjectConfigAuthTests
{
    private const string MessageJson = """
        { "message": "hello from the test" }
        """;

    [Fact]
    public async Task PostMessage_WithStaticKey_IsStillAcceptedWhenProcessorHasOtherKeys()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, "processor-key", 10)),
            DateTimeOffset.UtcNow);

        using var response = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            RelayTestClient.ValidKey);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(RelayTestClient.ProjectId, Assert.Single(publisher.Envelopes).ProjectId);
    }

    [Fact]
    public async Task PostMessage_WithCachedProcessorKey_IsAcceptedForUnknownStaticProject()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (42, "processor-only-key", 4)),
            DateTimeOffset.UtcNow);

        using var response = await RelayTestClient.PostJsonAsync(
            client,
            RelayTestClient.MessagesPath(42),
            MessageJson,
            "processor-only-key");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(42, envelope.ProjectId);

        var authorization = provider.Authorize(42, "processor-only-key");
        Assert.NotNull(authorization);
        Assert.Equal(4, authorization.RateLimitPerMinute);
    }

    [Fact]
    public async Task PostMessage_AfterProcessorConfigRefresh_AcceptsCachedKey()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        await factory.ProjectConfigHandler.WaitForRequestCountAsync(1);
        factory.ProjectConfigHandler.EnqueueConfig("v1", (42, "processor-only-key", 4));
        var cache = factory.Services.GetRequiredService<ProjectConfigCache>();
        Assert.True(await cache.RefreshOnceAsync(CancellationToken.None));

        using var response = await RelayTestClient.PostJsonAsync(
            client,
            RelayTestClient.MessagesPath(42),
            MessageJson,
            "processor-only-key");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(42, Assert.Single(publisher.Envelopes).ProjectId);
    }

    [Fact]
    public async Task PostMessage_WithUnknownKey_ReturnsUnauthorized()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var response = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            "not-a-real-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_AfterRefresh_AcceptsKeyCreatedSincePreviousRefresh()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, "existing-key", null)),
            DateTimeOffset.UtcNow);

        using (var rejected = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            "new-key"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        // A successful refresh clears the negative cache, so the key must work
        // immediately even though the same pair was rejected moments ago.
        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v2", (1, "new-key", null)),
            DateTimeOffset.UtcNow);

        using var accepted = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            "new-key");

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Single(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_AfterRefresh_RejectsRevokedKey()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, "revoked-key", null)),
            DateTimeOffset.UtcNow);

        using (var accepted = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            "revoked-key"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v2", (1, "replacement-key", null)),
            DateTimeOffset.UtcNow);

        using var rejected = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            "revoked-key");

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Single(publisher.Envelopes);
    }

    [Fact]
    public void Authorize_WhenStaticAndCachedConfigurationKnowKey_UsesCachedRateLimit()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot(
                "v1",
                (1, RelayTestClient.ValidKey, 17),
                (1, "cached-only-key", null)),
            DateTimeOffset.UtcNow);

        var both = provider.Authorize(1, RelayTestClient.ValidKey);
        Assert.NotNull(both);
        Assert.Equal(1, both.ProjectId);
        Assert.Equal(RelayTestClient.ValidKey, both.Key);
        Assert.Equal(17, both.RateLimitPerMinute);

        var cachedOnly = provider.Authorize(1, "cached-only-key");
        Assert.NotNull(cachedOnly);
        Assert.Null(cachedOnly.RateLimitPerMinute);
    }

    [Fact]
    public void Authorize_ForUnknownKey_ReturnsNull()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, "known-key", 5)),
            DateTimeOffset.UtcNow);

        Assert.Null(provider.Authorize(1, "not-cached"));
        Assert.Null(provider.Authorize(2, "known-key"));
    }

    [Fact]
    public void Authorize_ForStaticOnlyKey_ReturnsNullRateLimit()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, "other-key", 5)),
            DateTimeOffset.UtcNow);

        var authorization = provider.Authorize(1, RelayTestClient.ValidKey);

        Assert.NotNull(authorization);
        Assert.Null(authorization.RateLimitPerMinute);
    }

    [Fact]
    public void Authorize_WithMissingOrEmptyKey_ReturnsNull()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());
        var provider = GetProvider(factory);

        Assert.Null(provider.Authorize(1, null));
        Assert.Null(provider.Authorize(1, string.Empty));
    }

    private static ProjectConfigProvider GetProvider(RelayApplicationFactory factory) =>
        factory.Services.GetRequiredService<ProjectConfigProvider>();
}
