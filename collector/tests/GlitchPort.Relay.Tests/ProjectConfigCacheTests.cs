using System.Net;
using GlitchPort.Relay.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class ProjectConfigCacheTests
{
    [Fact]
    public async Task RefreshOnceAsync_LoadsSnapshotAndSendsInternalHeader()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueConfig("v1", (7, "processor-key", 5));

        var refreshed = await harness.Cache.RefreshOnceAsync(CancellationToken.None);

        Assert.True(refreshed);
        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "http://processor-a:5090/internal/project-configs",
            request.RequestUri.ToString());
        Assert.Equal("internal-secret", request.InternalSecret);
        Assert.Null(request.IfNoneMatch);

        Assert.Equal("processor", harness.Provider.Source);
        Assert.NotNull(harness.Provider.LastRefresh);
        Assert.Equal("v1", harness.Provider.Snapshot.Version);

        var authorization = harness.Provider.Authorize(7, "processor-key");
        Assert.NotNull(authorization);
        Assert.Equal(7, authorization.ProjectId);
        Assert.Equal("processor-key", authorization.Key);
        Assert.Equal(5, authorization.RateLimitPerMinute);
    }

    [Fact]
    public async Task RefreshOnceAsync_TriesEndpointsInOrderUntilSuccess()
    {
        using var harness = new CacheHarness(
            [
                "http://processor-a:5090",
                "http://processor-b:5090",
                "http://processor-c:5090",
            ]);
        harness.Handler
            .Enqueue(_ => throw new HttpRequestException("connection refused"))
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueConfig("v1", (1, "processor-key", null));

        var refreshed = await harness.Cache.RefreshOnceAsync(CancellationToken.None);

        Assert.True(refreshed);
        Assert.Equal(
            [
                "http://processor-a:5090",
                "http://processor-b:5090",
                "http://processor-c:5090",
            ],
            harness.Handler.Requests
                .Select(request => request.RequestUri.GetLeftPart(UriPartial.Authority))
                .ToArray());
        Assert.NotNull(harness.Provider.Authorize(1, "processor-key"));
    }

    [Fact]
    public async Task RefreshOnceAsync_WhenVersionIsKnown_SendsIfNoneMatchAndKeepsSnapshotOn304()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueConfig("v1", (1, "processor-key", 9));
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));

        var firstRefresh = harness.Provider.LastRefresh;
        var originalSnapshot = harness.Provider.Snapshot;

        harness.Handler.EnqueueStatus(HttpStatusCode.NotModified);
        var refreshed = await harness.Cache.RefreshOnceAsync(CancellationToken.None);

        Assert.True(refreshed);
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal("\"v1\"", harness.Handler.Requests[1].IfNoneMatch);
        Assert.Same(originalSnapshot, harness.Provider.Snapshot);
        Assert.Equal("v1", harness.Provider.Snapshot.Version);
        Assert.Equal("processor", harness.Provider.Source);
        Assert.NotNull(harness.Provider.LastRefresh);
        Assert.True(harness.Provider.LastRefresh >= firstRefresh);

        var authorization = harness.Provider.Authorize(1, "processor-key");
        Assert.NotNull(authorization);
        Assert.Equal(9, authorization.RateLimitPerMinute);
    }

    [Fact]
    public async Task RefreshOnceAsync_When304_ClearsUnknownKeyNegativeCache()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueConfig("v1", (1, "processor-key", null));
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));

        Assert.Null(harness.Provider.Authorize(1, "unknown-key"));
        Assert.Null(harness.Provider.Authorize(1, "unknown-key"));
        Assert.Equal(1, harness.Provider.UnknownKeyCacheHits);
        Assert.Equal(1, harness.Provider.UnknownKeyCacheCount);

        harness.Handler.EnqueueStatus(HttpStatusCode.NotModified);
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));

        Assert.Equal(0, harness.Provider.UnknownKeyCacheCount);

        // The next lookup misses again instead of being served negatively.
        Assert.Null(harness.Provider.Authorize(1, "unknown-key"));
        Assert.Equal(1, harness.Provider.UnknownKeyCacheHits);
        Assert.Equal(1, harness.Provider.UnknownKeyCacheCount);
    }

    [Fact]
    public async Task RefreshOnceAsync_WhenFirstResponseIs304_CountsAsSuccessfulRefresh()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueStatus(HttpStatusCode.NotModified);

        var refreshed = await harness.Cache.RefreshOnceAsync(CancellationToken.None);

        Assert.True(refreshed);
        Assert.Equal("processor", harness.Provider.Source);
        Assert.NotNull(harness.Provider.LastRefresh);
        Assert.Equal(0, harness.Provider.Snapshot.ProjectCount);

        // A 304 without a snapshot leaves the empty configuration active: no
        // key is accepted until the Processor actually returns a snapshot.
        Assert.Null(harness.Provider.Authorize(1, "legacy-key"));
    }

    [Fact]
    public async Task RefreshOnceAsync_WhenRefreshFailsAfterSuccess_KeepsSnapshotAndProcessorSource()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueConfig("v1", (1, "processor-key", null));
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));

        var originalSnapshot = harness.Provider.Snapshot;
        var lastRefresh = harness.Provider.LastRefresh;

        var refreshed = await harness.Cache.RefreshOnceAsync(CancellationToken.None);

        Assert.False(refreshed);
        Assert.Same(originalSnapshot, harness.Provider.Snapshot);
        Assert.Equal(lastRefresh, harness.Provider.LastRefresh);
        Assert.Equal("processor", harness.Provider.Source);
        Assert.NotNull(harness.Provider.Authorize(1, "processor-key"));
    }

    [Fact]
    public async Task RefreshOnceAsync_WhenJsonIsInvalid_KeepsPreviousSnapshot()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueConfig("v1", (1, "processor-key", null));
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));

        var originalSnapshot = harness.Provider.Snapshot;
        harness.Handler.EnqueueJson("{ this is not valid json");

        var refreshed = await harness.Cache.RefreshOnceAsync(CancellationToken.None);

        Assert.False(refreshed);
        Assert.Same(originalSnapshot, harness.Provider.Snapshot);
        Assert.NotNull(harness.Provider.Authorize(1, "processor-key"));
    }

    [Fact]
    public async Task RefreshOnceAsync_WhenNoRefreshEverSucceeded_ReportsNoneAndAuthorizesNothing()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);

        var refreshed = await harness.Cache.RefreshOnceAsync(CancellationToken.None);

        Assert.False(refreshed);
        Assert.Equal("none", harness.Provider.Source);
        Assert.Null(harness.Provider.LastRefresh);
        Assert.Equal(0, harness.Provider.Snapshot.ProjectCount);
        Assert.Equal(0, harness.Provider.ProjectCount);
        Assert.Null(harness.Provider.Authorize(1, "legacy-key"));
        Assert.Null(harness.Provider.Authorize(7, "processor-key"));
    }

    [Fact]
    public async Task RefreshOnceAsync_AfterRefresh_ClearsUnknownKeyNegativeCache()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueConfig("v1", (1, "known-key", null));
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));

        Assert.Null(harness.Provider.Authorize(1, "new-key"));

        harness.Handler.EnqueueConfig("v2", (1, "new-key", 3));
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));

        var authorization = harness.Provider.Authorize(1, "new-key");
        Assert.NotNull(authorization);
        Assert.Equal(3, authorization.RateLimitPerMinute);
    }

    [Fact]
    public async Task RefreshOnceAsync_AfterRefresh_RejectsRevokedKeys()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueConfig("v1", (1, "revoked-key", null));
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));
        Assert.NotNull(harness.Provider.Authorize(1, "revoked-key"));

        harness.Handler.EnqueueConfig("v2", (1, "replacement-key", null));
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));

        Assert.Null(harness.Provider.Authorize(1, "revoked-key"));
        Assert.NotNull(harness.Provider.Authorize(1, "replacement-key"));
    }

    [Fact]
    public void Authorize_UnknownKey_IsNegativelyCachedUntilItExpires()
    {
        var start = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(start);
        var provider = new ProjectConfigProvider(
            new RelayOptions { UnknownKeyCacheSeconds = 30 },
            clock);

        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Equal(1, provider.UnknownKeyCacheCount);
        Assert.Equal(0, provider.UnknownKeyCacheHits);

        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Equal(1, provider.UnknownKeyCacheHits);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Equal(2, provider.UnknownKeyCacheHits);

        // At the expiry instant the pair is looked up again and re-cached.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Equal(2, provider.UnknownKeyCacheHits);
        Assert.Equal(1, provider.UnknownKeyCacheCount);

        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Equal(3, provider.UnknownKeyCacheHits);
    }

    [Fact]
    public void Authorize_WithUnknownKeyCacheDisabled_DoesNotCacheUnknownKeys()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
        var provider = new ProjectConfigProvider(
            new RelayOptions { UnknownKeyCacheSeconds = 0 },
            clock);

        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Null(provider.Authorize(1, "unknown-key"));

        Assert.Equal(0, provider.UnknownKeyCacheCount);
        Assert.Equal(0, provider.UnknownKeyCacheHits);
    }

    [Fact]
    public void Authorize_WithKeyLongerThanCacheLimit_DoesNotStoreIt()
    {
        var provider = new ProjectConfigProvider(new RelayOptions());
        var longKey = new string('k', ProjectConfigProvider.MaxUnknownKeyCacheKeyLength + 1);

        Assert.Null(provider.Authorize(1, longKey));
        Assert.Null(provider.Authorize(1, longKey));

        Assert.Equal(0, provider.UnknownKeyCacheCount);
        Assert.Equal(0, provider.UnknownKeyCacheHits);
    }

    [Fact]
    public void Authorize_WithOverlongSnapshotKey_AcceptsTheKey()
    {
        // The negative-cache length limit only bounds attacker-controlled
        // values; a key that the Processor snapshot contains is always valid.
        var longKey = new string('k', ProjectConfigProvider.MaxUnknownKeyCacheKeyLength + 1);
        var provider = new ProjectConfigProvider(new RelayOptions());
        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, longKey, null)),
            DateTimeOffset.UtcNow);

        var authorization = provider.Authorize(1, longKey);

        Assert.NotNull(authorization);
        Assert.Equal(longKey, authorization.Key);
        Assert.Equal(0, provider.UnknownKeyCacheCount);
    }

    [Fact]
    public void Authorize_WithManyDistinctUnknownKeys_KeepsNegativeCacheBounded()
    {
        var provider = new ProjectConfigProvider(new RelayOptions());
        var attempts = ProjectConfigProvider.MaxUnknownKeyCacheEntries * 2;

        for (var index = 0; index < attempts; index++)
        {
            Assert.Null(provider.Authorize(1, $"unknown-{index}"));
        }

        Assert.InRange(
            provider.UnknownKeyCacheCount,
            1,
            ProjectConfigProvider.MaxUnknownKeyCacheEntries);
    }

    [Fact]
    public void MarkRefreshed_ClearsTheUnknownKeyNegativeCache()
    {
        var provider = new ProjectConfigProvider(new RelayOptions());
        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Equal(1, provider.UnknownKeyCacheHits);
        Assert.Equal(1, provider.UnknownKeyCacheCount);

        provider.MarkRefreshed(DateTimeOffset.UtcNow);

        Assert.Equal(0, provider.UnknownKeyCacheCount);
        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Equal(1, provider.UnknownKeyCacheHits);
    }

    [Fact]
    public void ApplySnapshot_ClearsTheUnknownKeyNegativeCache()
    {
        var provider = new ProjectConfigProvider(new RelayOptions());
        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Null(provider.Authorize(1, "unknown-key"));
        Assert.Equal(1, provider.UnknownKeyCacheHits);

        provider.ApplySnapshot(
            ProjectConfigTestData.Snapshot("v1", (1, "unknown-key", 4)),
            DateTimeOffset.UtcNow);

        Assert.Equal(0, provider.UnknownKeyCacheCount);
        var authorization = provider.Authorize(1, "unknown-key");
        Assert.NotNull(authorization);
        Assert.Equal(4, authorization.RateLimitPerMinute);
    }

    [Fact]
    public async Task RefreshOnceAsync_WhenBodyExceedsResponseLimit_KeepsPreviousSnapshot()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueConfig("v1", (1, "processor-key", null));
        Assert.True(await harness.Cache.RefreshOnceAsync(CancellationToken.None));

        var originalSnapshot = harness.Provider.Snapshot;
        harness.Handler.EnqueueOversizedResponse();

        var refreshed = await harness.Cache.RefreshOnceAsync(CancellationToken.None);

        Assert.False(refreshed);
        Assert.Same(originalSnapshot, harness.Provider.Snapshot);
        Assert.NotNull(harness.Provider.Authorize(1, "processor-key"));
    }

    [Fact]
    public async Task RefreshOnceAsync_WhenFirstEndpointBodyIsTooLarge_TriesNextEndpoint()
    {
        using var harness = new CacheHarness(
            ["http://processor-a:5090", "http://processor-b:5090"]);
        harness.Handler
            .EnqueueOversizedResponse()
            .EnqueueConfig("v1", (1, "processor-key", null));

        var refreshed = await harness.Cache.RefreshOnceAsync(CancellationToken.None);

        Assert.True(refreshed);
        Assert.Equal(
            ["http://processor-a:5090", "http://processor-b:5090"],
            harness.Handler.Requests
                .Select(request => request.RequestUri.GetLeftPart(UriPartial.Authority))
                .ToArray());
        Assert.Equal("v1", harness.Provider.Snapshot.Version);
    }

    [Fact]
    public void RefreshInterval_UsesConfiguredRefreshSeconds()
    {
        using var harness = new CacheHarness(
            ["http://processor-a:5090"],
            configRefreshSeconds: 7);

        Assert.Equal(TimeSpan.FromSeconds(7), harness.Cache.RefreshInterval);
    }

    [Fact]
    public async Task StartAsync_PerformsImmediateRefresh()
    {
        using var harness = new CacheHarness(["http://processor-a:5090"]);
        harness.Handler.EnqueueConfig("v1", (1, "processor-key", null));

        await harness.Cache.StartAsync(CancellationToken.None);
        try
        {
            await harness.Handler.WaitForRequestCountAsync(1);
            await WaitForProviderAsync(harness.Provider);

            var request = Assert.Single(harness.Handler.Requests);
            Assert.Equal(
                "http://processor-a:5090/internal/project-configs",
                request.RequestUri.ToString());
            Assert.Equal(TimeSpan.FromSeconds(60), harness.Cache.RefreshInterval);
            Assert.Equal("processor", harness.Provider.Source);
            Assert.Equal("v1", harness.Provider.Snapshot.Version);
        }
        finally
        {
            await harness.Cache.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(1, 60, 1.0)]
    [InlineData(2, 60, 2.0)]
    [InlineData(3, 60, 4.0)]
    [InlineData(4, 60, 8.0)]
    [InlineData(7, 60, 60.0)]
    [InlineData(8, 60, 60.0)]
    [InlineData(int.MaxValue, 60, 60.0)]
    [InlineData(0, 60, 1.0)]
    [InlineData(-5, 60, 1.0)]
    [InlineData(1, 3, 1.0)]
    [InlineData(2, 3, 2.0)]
    [InlineData(3, 3, 3.0)]
    [InlineData(10, 3, 3.0)]
    public void GetStartupRetryDelay_DoublesPerAttemptAndIsCappedAtRefreshInterval(
        int failedAttempts,
        int refreshIntervalSeconds,
        double expectedSeconds)
    {
        var delay = ProjectConfigCache.GetStartupRetryDelay(
            failedAttempts,
            TimeSpan.FromSeconds(refreshIntervalSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public async Task StartAsync_WhileProcessorIsDown_RetriesWithExponentialBackoff()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        using var harness = new CacheHarness(["http://processor-a:5090"], timeProvider: clock);
        harness.Handler
            .Enqueue(_ => throw new HttpRequestException("connection refused"))
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .Enqueue(_ => throw new HttpRequestException("connection refused"))
            .EnqueueConfig("v1", (1, "processor-key", null));
        var startedAt = clock.GetUtcNow();

        await harness.Cache.StartAsync(CancellationToken.None);
        try
        {
            await harness.Handler.WaitForRequestCountAsync(1);

            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            await harness.Handler.WaitForRequestCountAsync(2);

            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(2));
            clock.Advance(TimeSpan.FromSeconds(2));
            await harness.Handler.WaitForRequestCountAsync(3);

            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(4));
            clock.Advance(TimeSpan.FromSeconds(4));
            await harness.Handler.WaitForRequestCountAsync(4);

            // The periodic timer is only created after the successful refresh
            // applied the snapshot, so this wait also replaces polling.
            await clock.WaitForTimerAsync(harness.Cache.RefreshInterval);

            Assert.Equal(ProjectConfigProvider.ProcessorSource, harness.Provider.Source);
            Assert.Equal("v1", harness.Provider.Snapshot.Version);
            Assert.Equal(TimeSpan.FromSeconds(7), clock.GetUtcNow() - startedAt);
        }
        finally
        {
            await harness.Cache.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AfterFirstSuccess_DoesNotUseStartupBackoffAndKeepsSnapshot()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<ProjectConfigCache>();
        using var harness = new CacheHarness(
            ["http://processor-a:5090"],
            timeProvider: clock,
            logger: logger);
        harness.Handler.EnqueueConfig("v1", (1, "processor-key", null));

        await harness.Cache.StartAsync(CancellationToken.None);
        try
        {
            // The first refresh succeeds immediately and arms the periodic timer.
            await clock.WaitForTimerAsync(harness.Cache.RefreshInterval);
            Assert.Equal(ProjectConfigProvider.ProcessorSource, harness.Provider.Source);
            var snapshot = harness.Provider.Snapshot;

            // The next tick fails (the fake handler answers 500 by default).
            clock.Advance(harness.Cache.RefreshInterval);
            await harness.Handler.WaitForRequestCountAsync(2);
            await logger.WaitForEntryAsync(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains(
                    "keeping the last known configuration",
                    StringComparison.Ordinal));

            // A failed periodic refresh still warns exactly once through the
            // cache; the client reports the endpoint failure at debug level.
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.DoesNotContain(
                logger.Entries,
                entry => entry.Message.Contains("startup attempt", StringComparison.Ordinal));
            Assert.DoesNotContain(
                harness.ClientLogger.Entries,
                entry => entry.Level >= LogLevel.Warning);
            Assert.Contains(
                harness.ClientLogger.Entries,
                entry => entry.Level == LogLevel.Debug
                    && entry.Message.Contains("returned HTTP 500", StringComparison.Ordinal));

            // A failed refresh after the first success must not schedule a 1s,
            // 2s or 4s startup retry: nothing happens before the interval ends.
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.Equal(2, harness.Handler.Requests.Count);

            clock.Advance(TimeSpan.FromSeconds(1));
            await harness.Handler.WaitForRequestCountAsync(3);

            Assert.Equal(ProjectConfigProvider.ProcessorSource, harness.Provider.Source);
            Assert.Same(snapshot, harness.Provider.Snapshot);
            Assert.Equal("v1", harness.Provider.Snapshot.Version);
            Assert.NotNull(harness.Provider.Authorize(1, "processor-key"));
        }
        finally
        {
            await harness.Cache.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_WhileProcessorKeepsFailing_LogsWarningOnlyAtFirstAndTenthAttempt()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<ProjectConfigCache>();
        using var harness = new CacheHarness(
            ["http://processor-a:5090"],
            timeProvider: clock,
            logger: logger);

        await harness.Cache.StartAsync(CancellationToken.None);
        try
        {
            await harness.Handler.WaitForRequestCountAsync(1);

            // Attempts 1-9 schedule 1s, 2s, 4s, 8s, 16s, 32s and then the
            // refresh interval (60s), so attempt 10 is the second warning.
            double[] retryDelays = [1, 2, 4, 8, 16, 32, 60, 60, 60];

            for (var attempt = 1; attempt < 10; attempt++)
            {
                var delay = TimeSpan.FromSeconds(retryDelays[attempt - 1]);
                await clock.WaitForTimerAsync(delay);
                clock.Advance(delay);
                await harness.Handler.WaitForRequestCountAsync(attempt + 1);
            }

            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(60));

            var retryEntries = logger.Entries
                .Where(entry => entry.Message.Contains("startup attempt", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(10, retryEntries.Length);

            var warnings = retryEntries
                .Where(entry => entry.Level == LogLevel.Warning)
                .ToArray();

            Assert.Equal(2, warnings.Length);
            Assert.Contains("startup attempt 1", warnings[0].Message, StringComparison.Ordinal);
            Assert.Contains("retrying in 1s", warnings[0].Message, StringComparison.Ordinal);
            Assert.Contains("startup attempt 10", warnings[1].Message, StringComparison.Ordinal);
            Assert.Contains("retrying in 60s", warnings[1].Message, StringComparison.Ordinal);
            Assert.All(
                retryEntries.Where(entry => entry.Level != LogLevel.Warning),
                entry => Assert.Equal(LogLevel.Debug, entry.Level));
            Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));

            // The client keeps the per-endpoint failure detail visible, but at
            // debug level only: attempts 2-9 must not warn, so at the default
            // Information level the two cache warnings above are the only ones.
            Assert.DoesNotContain(
                harness.ClientLogger.Entries,
                entry => entry.Level >= LogLevel.Warning);

            var clientFailures = harness.ClientLogger.Entries
                .Where(entry => entry.Message.Contains("returned HTTP 500", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(10, clientFailures.Length);
            Assert.All(
                clientFailures,
                entry =>
                {
                    Assert.Equal(LogLevel.Debug, entry.Level);
                    Assert.Contains("processor-a:5090", entry.Message, StringComparison.Ordinal);
                });
        }
        finally
        {
            await harness.Cache.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForProviderAsync(ProjectConfigProvider provider)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (provider.Source != ProjectConfigProvider.ProcessorSource)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The immediate project-config refresh did not complete.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class CacheHarness : IDisposable
    {
        private readonly HttpClient _httpClient;

        public CacheHarness(
            string[] endpoints,
            int configRefreshSeconds = 60,
            TimeProvider? timeProvider = null,
            ILogger<ProjectConfigCache>? logger = null)
        {
            Handler = new FakeProjectConfigHandler();
            Options = new RelayOptions
            {
                ProcessorEndpoints = endpoints,
                InternalSharedSecret = "internal-secret",
                ConfigRefreshSeconds = configRefreshSeconds,
            };
            Provider = new ProjectConfigProvider(Options, timeProvider);
            ClientLogger = new RecordingLogger<ProjectConfigClient>();
            _httpClient = new HttpClient(Handler);
            var client = new ProjectConfigClient(_httpClient, ClientLogger);
            var cacheLogger = logger ?? NullLogger<ProjectConfigCache>.Instance;

            // Tests without a fake clock keep the four-argument construction so
            // the optional clock parameter stays source compatible.
            Cache = timeProvider is null
                ? new ProjectConfigCache(client, Provider, Options, cacheLogger)
                : new ProjectConfigCache(client, Provider, Options, cacheLogger, timeProvider);
        }

        public FakeProjectConfigHandler Handler { get; }

        public RelayOptions Options { get; }

        public ProjectConfigProvider Provider { get; }

        /// <summary>Log entries written by the project-config client.</summary>
        public RecordingLogger<ProjectConfigClient> ClientLogger { get; }

        public ProjectConfigCache Cache { get; }

        public void Dispose()
        {
            Cache.Dispose();
            _httpClient.Dispose();
        }
    }
}
