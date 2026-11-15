using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    /// <summary>
    /// Covers the HTTP relay's on-disk offline queue: when an event is written,
    /// how the backlog is drained, how <c>Retry-After</c> suspends traffic and how
    /// the directory lock behaves across relay instances.
    /// </summary>
    public sealed class OfflineQueueRelayTests
    {
        private const string DsnString = "http://public-key@relay.example.com:5080/1";

        private const string FileNameTimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

        [Fact]
        public async Task Handle_WhenTheNetworkFails_PersistsTheExactJsonAndReportsItQueued()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var handler = new ScriptableHttpMessageHandler(
                    (_, _) => throw new HttpRequestException("network down"));
                using var httpClient = new HttpClient(handler);
                using var relay = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);
                var captured = CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow));

                Assert.True(relay.TryEnqueue(captured));
                var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                var expectedPath = Path.Combine(Path.GetFullPath(directory), ExpectedFileName(captured));
                Assert.True(File.Exists(expectedPath), "The event was not written to the offline queue.");
                Assert.Equal(captured.Json, File.ReadAllText(expectedPath));

                Assert.Equal(1, result.Queued);
                Assert.Equal(0, result.Failed);
                Assert.Equal(0, result.Delivered);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Handle_WhenTheRelayRejectsTheEvent_PersistsNothingAndCountsFailed()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var handler = new ScriptableHttpMessageHandler(
                    (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.BadRequest, "bad event"));
                using var httpClient = new HttpClient(handler);
                using var relay = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);

                Assert.True(relay.TryEnqueue(CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow))));
                var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Empty(EventFiles(directory));
                Assert.Equal(0, result.Queued);
                Assert.Equal(1, result.Failed);
                Assert.Equal(1, handler.Attempts);
                Assert.Contains("400", result.LastError, StringComparison.Ordinal);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task FlushAsync_ResendsQueuedEventsOldestFirstAndDeletesThem()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var handler = new ScriptableHttpMessageHandler(
                    (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.InternalServerError, "relay down"));
                using var httpClient = new HttpClient(handler);
                using var relay = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);

                var older = CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow.AddSeconds(-3)));
                var newer = CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow.AddSeconds(-2)));

                Assert.True(relay.TryEnqueue(older));
                Assert.True(relay.TryEnqueue(newer));

                var queued = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(2, queued.Queued);
                Assert.Equal(0, queued.Failed);
                Assert.Equal(2, EventFiles(directory).Length);

                handler.Respond(HttpStatusCode.Accepted);

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(2, result.Delivered);
                Assert.Equal(0, result.Queued);
                Assert.Equal(0, result.Failed);
                Assert.Empty(EventFiles(directory));
                Assert.Equal(new[] { older.Json, newer.Json }, handler.SuccessfulBodies);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Handle_WithRetryAfter_QueuesTheEventAndWaitsOutTheHold()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var handler = new ScriptableHttpMessageHandler(
                    (_, _) => ScriptableHttpMessageHandler.WithRetryAfter(
                        TestHttpMessageHandler.Response(HttpStatusCode.TooManyRequests),
                        TimeSpan.FromSeconds(1)));
                using var httpClient = new HttpClient(handler);
                using var relay = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);
                var captured = CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow));

                Assert.True(relay.TryEnqueue(captured));
                var queued = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                // The immediate second attempt was skipped because the header asked
                // for a longer pause than the 200 ms retry delay.
                Assert.Equal(1, handler.Attempts);
                Assert.Equal(1, queued.Queued);
                Assert.Equal(0, queued.Failed);
                Assert.Single(EventFiles(directory));

                // While the hold is active no request reaches the relay.
                var duringHold = await relay.FlushAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, handler.Attempts);
                Assert.Equal(0, duringHold.Delivered);

                handler.Respond(HttpStatusCode.Accepted);
                await Task.Delay(TimeSpan.FromMilliseconds(1100));

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(1, result.Delivered);
                Assert.Equal(0, result.Queued);
                Assert.Empty(EventFiles(directory));
                Assert.Equal(new[] { captured.Json }, handler.SuccessfulBodies);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task FlushAsync_WhenARateLimitHasAnHttpDateRetryAfter_ResumesAfterTheHold()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var handler = new ScriptableHttpMessageHandler(
                    (_, _) => throw new HttpRequestException("network down"));
                using var httpClient = new HttpClient(handler);
                using var relay = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);

                Assert.True(relay.TryEnqueue(CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow))));
                var queued = await relay.FlushAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, queued.Queued);

                // The drain pass meets a 503 whose Retry-After is an HTTP date two
                // seconds ahead.
                handler.Respond((_, _) => ScriptableHttpMessageHandler.WithRetryAfterDate(
                    TestHttpMessageHandler.Response(HttpStatusCode.ServiceUnavailable),
                    DateTimeOffset.UtcNow.AddSeconds(2)));

                var attemptsBeforeDrain = handler.Attempts;
                var held = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(attemptsBeforeDrain + 1, handler.Attempts);
                Assert.Equal(1, held.Queued);
                Assert.Equal(0, held.Failed);

                var duringHold = await relay.FlushAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(attemptsBeforeDrain + 1, handler.Attempts);
                Assert.Equal(0, duringHold.Delivered);

                handler.Respond(HttpStatusCode.Accepted);
                await Task.Delay(TimeSpan.FromMilliseconds(2300));

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(1, result.Delivered);
                Assert.Equal(0, result.Queued);
                Assert.Empty(EventFiles(directory));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Handle_WhileARateLimitHoldIsActive_QueuesWithoutSending()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var handler = new ScriptableHttpMessageHandler(
                    (_, _) => ScriptableHttpMessageHandler.WithRetryAfter(
                        TestHttpMessageHandler.Response(HttpStatusCode.TooManyRequests),
                        TimeSpan.FromSeconds(5)));
                using var httpClient = new HttpClient(handler);
                using var relay = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);

                Assert.True(relay.TryEnqueue(CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow))));
                var first = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(1, handler.Attempts);
                Assert.Equal(1, first.Queued);

                // The hold is still active, so the next event is written to the disk
                // queue without touching the network.
                Assert.True(relay.TryEnqueue(CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow))));
                var second = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(1, handler.Attempts);
                Assert.Equal(2, second.Queued);
                Assert.Equal(0, second.Failed);
                Assert.Equal(2, EventFiles(directory).Length);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task FlushAsync_WhenARateLimitHasNoRetryAfterHeader_HoldsForFiveSeconds()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var handler = new ScriptableHttpMessageHandler(
                    (_, _) => throw new HttpRequestException("network down"));
                using var httpClient = new HttpClient(handler);
                using var relay = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);

                Assert.True(relay.TryEnqueue(CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow))));
                var queued = await relay.FlushAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, queued.Queued);

                // A 429 without a Retry-After must still suspend traffic.
                handler.Respond(HttpStatusCode.TooManyRequests);

                var attemptsBeforeDrain = handler.Attempts;
                var held = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(attemptsBeforeDrain + 1, handler.Attempts);
                Assert.Equal(1, held.Queued);
                Assert.Equal(0, held.Failed);

                var duringHold = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(attemptsBeforeDrain + 1, handler.Attempts);
                Assert.Equal(0, duringHold.Delivered);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Handle_WithMaxEvents_KeepsTheNewestEventsAndCountsDropped()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var handler = new ScriptableHttpMessageHandler(
                    (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.BadGateway, "relay down"));
                using var httpClient = new HttpClient(handler);
                using var relay = new HttpRelay(
                    Dsn.Parse(DsnString),
                    CreateQueueOptions(directory, maxEvents: 2),
                    httpClient);

                var baseTime = TruncateToMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-5));
                var first = CreateEvent(baseTime);
                var second = CreateEvent(baseTime.AddSeconds(1));
                var third = CreateEvent(baseTime.AddSeconds(2));

                Assert.True(relay.TryEnqueue(first));
                Assert.True(relay.TryEnqueue(second));
                Assert.True(relay.TryEnqueue(third));

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(2, result.Queued);
                Assert.Equal(0, result.Failed);
                Assert.True(result.Dropped >= 1, "The oldest queued event was not counted as dropped.");

                var files = EventFiles(directory);
                Assert.Equal(2, files.Length);
                Assert.Contains(Path.Combine(Path.GetFullPath(directory), ExpectedFileName(second)), files);
                Assert.Contains(Path.Combine(Path.GetFullPath(directory), ExpectedFileName(third)), files);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Constructor_WithAQueuedEventFromAPreviousRun_ResendsAndDeletesIt()
        {
            var directory = CreateTempDirectory();

            try
            {
                var captured = CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow));

                using (var failing = new ScriptableHttpMessageHandler(
                    (_, _) => throw new HttpRequestException("network down")))
                using (var failingClient = new HttpClient(failing))
                using (var relay = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), failingClient))
                {
                    Assert.True(relay.TryEnqueue(captured));
                    var queued = await relay.FlushAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(1, queued.Queued);
                }

                Assert.Single(EventFiles(directory));

                using var healthy = new ScriptableHttpMessageHandler(
                    (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.Accepted));
                using var httpClient = new HttpClient(healthy);
                using var restarted = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);

                var result = await restarted.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(1, result.Delivered);
                Assert.Equal(0, result.Queued);
                Assert.Empty(EventFiles(directory));
                Assert.Equal(new[] { captured.Json }, healthy.SuccessfulBodies);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Constructor_WhileTheDirectoryIsLocked_ReportsFailedAndWritesNoFiles()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var healthy = new ScriptableHttpMessageHandler(
                    (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.Accepted));
                using var httpClient = new HttpClient(healthy);

                var first = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);
                try
                {
                    Assert.True(File.Exists(Path.Combine(directory, DirectoryLock.LockFileName)));

                    using var failing = new ScriptableHttpMessageHandler(
                        (_, _) => throw new HttpRequestException("network down"));
                    using var failingClient = new HttpClient(failing);
                    using var second = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), failingClient);

                    Assert.True(second.TryEnqueue(CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow))));
                    var result = await second.FlushAsync(TimeSpan.FromSeconds(10));

                    Assert.Empty(EventFiles(directory));
                    Assert.Equal(1, result.Failed);
                    Assert.Equal(0, result.Queued);
                }
                finally
                {
                    first.Dispose();
                }

                // Once the first relay released the lock the directory is usable.
                using var recovered = new HttpRelay(Dsn.Parse(DsnString), CreateQueueOptions(directory), httpClient);

                Assert.True(recovered.TryEnqueue(CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow))));
                var delivered = await recovered.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(1, delivered.Delivered);
                Assert.Equal(0, delivered.Failed);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Handle_AfterASuccessfulSend_DrainsOlderQueuedEventsAndCountsEvictions()
        {
            var directory = CreateTempDirectory();

            try
            {
                using var handler = new ScriptableHttpMessageHandler(
                    (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.InternalServerError, "relay down"));
                using var httpClient = new HttpClient(handler);
                using var relay = new HttpRelay(
                    Dsn.Parse(DsnString),
                    CreateQueueOptions(directory, maxAgeHours: 1),
                    httpClient);

                var older = CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1)));
                Assert.True(relay.TryEnqueue(older));

                var queued = await relay.FlushAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, queued.Queued);

                // A file from an earlier run that outlived MaxAgeHours is evicted by
                // the next drain pass and counted as dropped.
                var staleTimestamp = TruncateToMilliseconds(DateTimeOffset.UtcNow.AddHours(-2));
                File.WriteAllText(
                    Path.Combine(directory, ExpectedFileName(staleTimestamp, Guid.NewGuid())),
                    "{\"stale\":true}");

                handler.Respond(HttpStatusCode.Accepted);

                var newer = CreateEvent(TruncateToMilliseconds(DateTimeOffset.UtcNow));
                Assert.True(relay.TryEnqueue(newer));

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(2, result.Delivered);
                Assert.Equal(1, result.Dropped);
                Assert.Equal(0, result.Queued);
                Assert.Empty(EventFiles(directory));
                Assert.Equal(new[] { newer.Json, older.Json }, handler.SuccessfulBodies);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static HttpRelayOptions CreateQueueOptions(
            string directory,
            int maxEvents = 500,
            int maxAgeHours = 72,
            int retryIntervalSeconds = 60)
        {
            var options = new HttpRelayOptions();
            options.OfflineQueue.Directory = directory;
            options.OfflineQueue.MaxEvents = maxEvents;
            options.OfflineQueue.MaxAgeHours = maxAgeHours;
            options.OfflineQueue.RetryIntervalSeconds = retryIntervalSeconds;
            return options;
        }

        private static CapturedEvent CreateEvent(DateTimeOffset timestamp, Guid? eventId = null)
        {
            var id = eventId ?? Guid.NewGuid();
            var json = "{\"event_id\":\""
                + id.ToString("D", CultureInfo.InvariantCulture)
                + "\",\"message\":\"hello\",\"timestamp\":\""
                + timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                + "\"}";

            return new CapturedEvent(
                id,
                timestamp,
                Level.Info,
                "hello",
                GlitchPortOptions.DefaultSdkName,
                "1.2.3",
                json);
        }

        /// <summary>Builds the queue file name the relay must use for an event.</summary>
        private static string ExpectedFileName(CapturedEvent e)
        {
            return ExpectedFileName(e.Timestamp, e.EventId);
        }

        private static string ExpectedFileName(DateTimeOffset timestamp, Guid eventId)
        {
            return timestamp.UtcDateTime.ToString(FileNameTimestampFormat, CultureInfo.InvariantCulture)
                + "-"
                + eventId.ToString("D", CultureInfo.InvariantCulture)
                + ".json";
        }

        private static string[] EventFiles(string directory)
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.json")
                : Array.Empty<string>();
        }

        private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
        {
            return new DateTimeOffset(
                value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond),
                value.Offset);
        }

        private static string CreateTempDirectory()
        {
            return Path.Combine(
                Path.GetTempPath(),
                "glitchport-offline-relay-tests",
                Guid.NewGuid().ToString("N"));
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
