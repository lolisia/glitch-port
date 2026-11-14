using System.Globalization;
using System.Net;
using GlitchPort.Collector.Contracts;
using GlitchPort.Relay.Config;
using GlitchPort.Relay.RateLimit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GlitchPort.Relay.Tests;

/// <summary>
/// Covers the token-bucket math of <see cref="ProjectRateLimiter"/> and the
/// 429 behaviour of the ingest endpoints. The unit tests drive an injectable
/// clock so refill and <c>Retry-After</c> assertions never depend on timing.
/// </summary>
public sealed class RateLimitTests
{
    private const string MessageJson = """
        { "message": "hello from the rate limit test" }
        """;

    private static readonly DateTimeOffset Start = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Acquire_AllowsExactlyTheProjectCapacity()
    {
        var limiter = CreateLimiter(3, new ManualTimeProvider(Start));

        for (var index = 0; index < 3; index++)
        {
            Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);
        }

        var rejected = limiter.Acquire(1, "key", rateLimitPerMinute: null);

        Assert.False(rejected.IsAllowed);
        Assert.True(rejected.RetryAfterSeconds >= 1);
    }

    [Fact]
    public void Acquire_RefillsContinuously()
    {
        var clock = new ManualTimeProvider(Start);
        var limiter = CreateLimiter(60, clock);
        Drain(limiter, 60);

        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);

        // One token per second: a single second buys exactly one request.
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);
        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);

        // A full window refills the bucket up to its capacity, not beyond it.
        clock.Advance(TimeSpan.FromSeconds(60));

        Drain(limiter, 60);
        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);
    }

    [Fact]
    public void Acquire_RetryAfterIsTheCeilingOfSecondsUntilNextToken()
    {
        var clock = new ManualTimeProvider(Start);
        var limiter = CreateLimiter(15, clock);
        Drain(limiter, 15);

        // 15 tokens per minute is one token every four seconds.
        Assert.Equal(4, limiter.Acquire(1, "key", rateLimitPerMinute: null).RetryAfterSeconds);

        // Two seconds of refill leave half a token: two more seconds are
        // needed and the ceiling stays 2.
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(2, limiter.Acquire(1, "key", rateLimitPerMinute: null).RetryAfterSeconds);

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);
    }

    [Fact]
    public void Acquire_RetryAfterIsAtLeastOneSecond()
    {
        var clock = new ManualTimeProvider(Start);
        var limiter = CreateLimiter(60, clock);
        Drain(limiter, 60);

        // 0.2 seconds of refill remain short of a token and the next one is
        // 0.8 seconds away, so the ceiling is one second.
        clock.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, limiter.Acquire(1, "key", rateLimitPerMinute: null).RetryAfterSeconds);
    }

    [Fact]
    public void Acquire_WithZeroProjectLimit_IsUnlimited()
    {
        var limiter = CreateLimiter(0, new ManualTimeProvider(Start));

        for (var index = 0; index < 1000; index++)
        {
            Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);
        }
    }

    [Fact]
    public void Acquire_WithZeroProjectAndKeyLimits_IsUnlimited()
    {
        var limiter = CreateLimiter(0, new ManualTimeProvider(Start));

        for (var index = 0; index < 1000; index++)
        {
            Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: 0).IsAllowed);
        }
    }

    [Fact]
    public void Acquire_WithZeroKeyLimit_OnlyAppliesTheProjectLimit()
    {
        var limiter = CreateLimiter(1, new ManualTimeProvider(Start));

        // The key limit is zero (unlimited), so the project limit is the only
        // bucket that rejects the second request.
        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: 0).IsAllowed);
        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: 0).IsAllowed);
    }

    [Fact]
    public void Acquire_WithoutKeyLimit_OnlyAppliesTheProjectLimit()
    {
        var limiter = CreateLimiter(1, new ManualTimeProvider(Start));

        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);
        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);
    }

    [Fact]
    public void Acquire_AppliesTheKeyLimitInAdditionToTheProjectLimit()
    {
        var limiter = CreateLimiter(100, new ManualTimeProvider(Start));

        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: 2).IsAllowed);
        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: 2).IsAllowed);

        var rejected = limiter.Acquire(1, "key", rateLimitPerMinute: 2);

        Assert.False(rejected.IsAllowed);
        Assert.InRange(rejected.RetryAfterSeconds, 1, 60);
    }

    [Fact]
    public void Acquire_TracksProjectsIndependently()
    {
        var limiter = CreateLimiter(1, new ManualTimeProvider(Start));

        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);
        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);

        Assert.True(limiter.Acquire(2, "key", rateLimitPerMinute: null).IsAllowed);
        Assert.False(limiter.Acquire(2, "key", rateLimitPerMinute: null).IsAllowed);
    }

    [Fact]
    public void Acquire_TracksKeysIndependently()
    {
        var limiter = CreateLimiter(100, new ManualTimeProvider(Start));

        Assert.True(limiter.Acquire(1, "key-a", rateLimitPerMinute: 1).IsAllowed);
        Assert.False(limiter.Acquire(1, "key-a", rateLimitPerMinute: 1).IsAllowed);

        // Another key in the same project has its own bucket.
        Assert.True(limiter.Acquire(1, "key-b", rateLimitPerMinute: 1).IsAllowed);
        Assert.False(limiter.Acquire(1, "key-b", rateLimitPerMinute: 1).IsAllowed);

        // The same key for another project has its own bucket as well.
        Assert.True(limiter.Acquire(2, "key-a", rateLimitPerMinute: 1).IsAllowed);
    }

    [Fact]
    public void Acquire_RefillsTheKeyBucketAfterAMinute()
    {
        var clock = new ManualTimeProvider(Start);
        var limiter = CreateLimiter(1000, clock);

        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: 1).IsAllowed);
        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: 1).IsAllowed);

        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: 1).IsAllowed);
        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: 1).IsAllowed);
    }

    [Fact]
    public void Acquire_RejectedRequestDoesNotConsumeTokensFromAnyBucket()
    {
        var clock = new ManualTimeProvider(Start);
        var limiter = CreateLimiter(1, clock);

        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: 1).IsAllowed);

        // Both buckets are empty, so this request is rejected. The rejection
        // must not consume from either bucket: one minute after the admitted
        // request both hold exactly one token again. If a rejected request had
        // consumed them, the clamped buckets would only be ~0.83 full.
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: 1).IsAllowed);

        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: 1).IsAllowed);
        Assert.False(limiter.Acquire(1, "key", rateLimitPerMinute: 1).IsAllowed);
    }

    [Fact]
    public void Acquire_ConcurrentCallsNeverExceedTheLimit()
    {
        var limiter = CreateLimiter(1000, new ManualTimeProvider(Start));
        var admitted = 0;

        Parallel.For(
            0,
            500,
            _ =>
            {
                if (limiter.Acquire(1, "key", rateLimitPerMinute: 10).IsAllowed)
                {
                    Interlocked.Increment(ref admitted);
                }
            });

        Assert.Equal(10, admitted);
    }

    [Fact]
    public async Task PostMessage_AfterProjectLimit_Returns429WithRetryAfterAndErrorBody()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = CreateProjectLimitedFactory(publisher, perProjectPerMinute: "2");
        using var client = factory.CreateClient();

        for (var index = 0; index < 2; index++)
        {
            using var accepted = await RelayTestClient.PostMessageAsync(
                client,
                MessageJson,
                RelayTestClient.ValidKey);

            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var rejected = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            RelayTestClient.ValidKey);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(ReadRetryAfter(rejected) >= 1);

        var error = await RelayTestClient.ReadJsonAsync(rejected);
        var property = Assert.Single(error.EnumerateObject());
        Assert.Equal("error", property.Name);
        Assert.Equal("rate limit exceeded", property.Value.GetString());

        Assert.Equal(2, publisher.Envelopes.Count);
    }

    [Fact]
    public async Task PostEvent_AfterProjectLimit_Returns429()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = CreateProjectLimitedFactory(publisher, perProjectPerMinute: "1");
        using var client = factory.CreateClient();

        using (var accepted = await RelayTestClient.PostEventAsync(
            client,
            """{ "message": "boom" }"""))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var rejected = await RelayTestClient.PostEventAsync(
            client,
            """{ "message": "boom" }""");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(ReadRetryAfter(rejected) >= 1);
        Assert.Single(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WhenRateLimited_DoesNotReadTheBody()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = CreateProjectLimitedFactory(publisher, perProjectPerMinute: "1");
        using var client = factory.CreateClient();

        using (var accepted = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            RelayTestClient.ValidKey))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        // An unsupported encoding is a 400 when the body is read, so a 429
        // proves the limit is enforced before the body is touched.
        using var rejected = await RelayTestClient.PostJsonAsync(
            client,
            RelayTestClient.MessagesPath(),
            MessageJson,
            RelayTestClient.ValidKey,
            contentEncoding: "br");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Single(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WithInvalidKey_ReturnsUnauthorizedBeforeTheRateLimit()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = CreateProjectLimitedFactory(publisher, perProjectPerMinute: "1");
        using var client = factory.CreateClient();

        using (var accepted = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            RelayTestClient.ValidKey))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        // The project bucket is now empty, but authentication must still be
        // decided first: invalid and missing keys are 401, never 429.
        using (var invalid = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            "not-a-real-key"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        }

        using (var missing = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            key: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        }

        Assert.Single(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WithZeroProjectLimit_IsUnlimited()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = CreateProjectLimitedFactory(publisher, perProjectPerMinute: "0");
        using var client = factory.CreateClient();

        for (var index = 0; index < 20; index++)
        {
            using var response = await RelayTestClient.PostMessageAsync(
                client,
                MessageJson,
                RelayTestClient.ValidKey);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Assert.Equal(20, publisher.Envelopes.Count);
    }

    [Fact]
    public async Task PostMessage_AfterKeyLimit_Returns429()
    {
        var publisher = new RecordingEnvelopePublisher();
        var handler = new FakeProjectConfigHandler();
        using var factory = new RelayApplicationFactory(publisher, handler);
        using var client = factory.CreateClient();

        await ApplyKeyRateLimitAsync(
            factory,
            handler,
            RelayTestClient.ValidKey,
            rateLimitPerMinute: 2);

        for (var index = 0; index < 2; index++)
        {
            using var accepted = await RelayTestClient.PostMessageAsync(
                client,
                MessageJson,
                RelayTestClient.ValidKey);

            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var rejected = await RelayTestClient.PostMessageAsync(
            client,
            MessageJson,
            RelayTestClient.ValidKey);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(ReadRetryAfter(rejected) >= 1);
        Assert.Equal(
            "rate limit exceeded",
            await RelayTestClient.ReadErrorAsync(rejected));
        Assert.Equal(2, publisher.Envelopes.Count);
    }

    [Fact]
    public async Task PostMessage_WithZeroKeyRateLimit_IsUnlimited()
    {
        var publisher = new RecordingEnvelopePublisher();
        var handler = new FakeProjectConfigHandler();
        using var factory = new RelayApplicationFactory(publisher, handler);
        using var client = factory.CreateClient();

        await ApplyKeyRateLimitAsync(
            factory,
            handler,
            RelayTestClient.ValidKey,
            rateLimitPerMinute: 0);

        for (var index = 0; index < 20; index++)
        {
            using var response = await RelayTestClient.PostMessageAsync(
                client,
                MessageJson,
                RelayTestClient.ValidKey);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Assert.Equal(20, publisher.Envelopes.Count);
    }

    private static ProjectRateLimiter CreateLimiter(
        int perProjectPerMinute,
        ManualTimeProvider clock) =>
        new(
            new RelayOptions { RateLimitPerProjectPerMinute = perProjectPerMinute },
            clock);

    private static void Drain(ProjectRateLimiter limiter, int count)
    {
        for (var index = 0; index < count; index++)
        {
            Assert.True(limiter.Acquire(1, "key", rateLimitPerMinute: null).IsAllowed);
        }
    }

    private static RelayApplicationFactory CreateProjectLimitedFactory(
        IEnvelopePublisher publisher,
        string perProjectPerMinute) =>
        new(
            publisher,
            relayConfiguration: new Dictionary<string, string?>
            {
                ["Relay:RateLimit:PerProjectPerMinute"] = perProjectPerMinute,
            });

    private static async Task ApplyKeyRateLimitAsync(
        RelayApplicationFactory factory,
        FakeProjectConfigHandler handler,
        string publicKey,
        int? rateLimitPerMinute)
    {
        // The startup refresh is the first request; wait for it before queuing
        // the response so the background service cannot consume it.
        await handler.WaitForRequestCountAsync(1);
        handler.EnqueueConfig(
            "v1",
            (RelayTestClient.ProjectId, publicKey, rateLimitPerMinute));

        var cache = factory.Services.GetRequiredService<ProjectConfigCache>();
        Assert.True(await cache.RefreshOnceAsync(CancellationToken.None));
    }

    private static int ReadRetryAfter(HttpResponseMessage response) =>
        int.Parse(
            Assert.Single(response.Headers.GetValues("Retry-After")),
            CultureInfo.InvariantCulture);
}
