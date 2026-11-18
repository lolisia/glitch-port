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
    public async Task PostMessage_WithKeyNotInSnapshot_ReturnsUnauthorized()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, "processor-key", 10)),
            DateTimeOffset.UtcNow);

        using var response = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            RelayTestClient.ValidKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WithProcessorKey_IsAcceptedForProject()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();
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
        using var client = await factory.CreateLoadedClientAsync();

        factory.ProjectConfigHandler.EnqueueConfig("v2", (42, "processor-only-key", 4));
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
        using var client = await factory.CreateLoadedClientAsync();

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
        using var client = await factory.CreateLoadedClientAsync();
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
        using var client = await factory.CreateLoadedClientAsync();
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
    public void Authorize_WhenSnapshotKnowsKey_UsesConfiguredRateLimit()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());
        using var client = factory.CreateClient();
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot(
                "v1",
                (1, RelayTestClient.ValidKey, 17),
                (1, "other-key", null)),
            DateTimeOffset.UtcNow);

        var withLimit = provider.Authorize(1, RelayTestClient.ValidKey);
        Assert.NotNull(withLimit);
        Assert.Equal(1, withLimit.ProjectId);
        Assert.Equal(RelayTestClient.ValidKey, withLimit.Key);
        Assert.Equal(17, withLimit.RateLimitPerMinute);

        var withoutLimit = provider.Authorize(1, "other-key");
        Assert.NotNull(withoutLimit);
        Assert.Null(withoutLimit.RateLimitPerMinute);
    }

    [Fact]
    public void Authorize_ForUnknownKey_ReturnsNull()
    {
        using var factory = new RelayApplicationFactory(new RecordingEnvelopePublisher());
        using var client = factory.CreateClient();
        var provider = GetProvider(factory);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, "known-key", 5)),
            DateTimeOffset.UtcNow);

        Assert.Null(provider.Authorize(1, "not-cached"));
        Assert.Null(provider.Authorize(2, "known-key"));
    }

    [Fact]
    public void Authorize_BeforeAnySnapshot_ReturnsNull()
    {
        using var factory = new RelayApplicationFactory(
            new RecordingEnvelopePublisher(),
            new FakeProjectConfigHandler());
        using var client = factory.CreateClient();
        var provider = GetProvider(factory);

        Assert.Equal(ProjectConfigProvider.NotLoadedSource, provider.Source);
        Assert.Null(provider.Authorize(1, "any-key"));
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
