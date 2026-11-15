using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class OfflineQueueTests
    {
        private const string Json = "{\"event_id\":\"9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e\",\"message\":\"hello \\\"world\\\" \\u00e9\"}";

        private static readonly Guid EventId = Guid.Parse("9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e");

        [Fact]
        public void TryEnqueue_WritesTheExactJsonToTheExpectedFileAtomically()
        {
            var directory = CreateTempDirectory();
            try
            {
                using var queue = new OfflineQueue(directory, 500, 72);
                Assert.True(queue.IsAvailable, queue.LastError);

                // The file name always uses the UTC timestamp, even when the event
                // timestamp carries a different offset. The timestamp is recent so
                // the age limit can never evict the file during the test.
                var timestamp = TruncateToMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-5))
                    .ToOffset(TimeSpan.FromHours(2));
                var expectedName = timestamp.ToUniversalTime().ToString(
                    "yyyyMMdd'T'HHmmssfff'Z'",
                    CultureInfo.InvariantCulture)
                    + "-"
                    + EventId.ToString("D")
                    + ".json";

                Assert.True(
                    queue.TryEnqueue(Json, EventId, timestamp, out var evicted, out var error),
                    error);

                Assert.Null(error);
                Assert.Equal(0, evicted);
                Assert.Equal(1, queue.Count);

                var expectedPath = Path.Combine(Path.GetFullPath(directory), expectedName);

                Assert.True(File.Exists(expectedPath));
                Assert.Equal(Json, File.ReadAllText(expectedPath));

                var bytes = File.ReadAllBytes(expectedPath);
                Assert.False(
                    bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    "The queue file must be UTF-8 without a byte order mark.");

                // The temp file is renamed into place, never left behind.
                Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
                Assert.Single(Directory.GetFiles(directory, "*.json"));

                Assert.True(queue.TryPeekOldest(out var entry));
                Assert.NotNull(entry);
                Assert.Equal(expectedPath, entry!.Path);
                Assert.Equal(EventId, entry.EventId);
                Assert.Equal(timestamp.UtcDateTime, entry.Timestamp.UtcDateTime);
                Assert.Equal(Json, entry.Json);

                queue.Remove(entry);
                Assert.Equal(0, queue.Count);
                Assert.False(File.Exists(expectedPath));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void TryEnqueue_WhenTheSameFileNameExists_AppendsANumericSuffix()
        {
            var directory = CreateTempDirectory();
            try
            {
                using var queue = new OfflineQueue(directory, 500, 72);
                var timestamp = TruncateToMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-5));
                var stem = timestamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
                    + "-"
                    + EventId.ToString("D");

                Assert.True(queue.TryEnqueue(Json, EventId, timestamp, out _, out var firstError), firstError);
                Assert.True(queue.TryEnqueue("{\"second\":true}", EventId, timestamp, out _, out var secondError), secondError);

                Assert.Equal(2, queue.Count);
                Assert.True(File.Exists(Path.Combine(directory, stem + ".json")));
                Assert.True(File.Exists(Path.Combine(directory, stem + "-1.json")));
                Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void TryPeekOldest_ReturnsTheEntriesOldestFirst()
        {
            var directory = CreateTempDirectory();
            try
            {
                using var queue = new OfflineQueue(directory, 500, 72);
                var baseTime = TruncateToMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
                var first = baseTime;
                var second = baseTime.AddSeconds(1);
                var third = baseTime.AddSeconds(2);

                // Enqueue out of order: the file names, not the write order, decide
                // which event is the oldest.
                Enqueue(queue, Guid.NewGuid(), second);
                Enqueue(queue, Guid.NewGuid(), first);
                Enqueue(queue, Guid.NewGuid(), third);

                Assert.Equal(3, queue.Count);
                Assert.Equal(first.UtcDateTime, PeekTimestamp(queue));
                queue.Remove(Peek(queue));
                Assert.Equal(second.UtcDateTime, PeekTimestamp(queue));
                queue.Remove(Peek(queue));
                Assert.Equal(third.UtcDateTime, PeekTimestamp(queue));
                queue.Remove(Peek(queue));
                Assert.False(queue.TryPeekOldest(out var empty));
                Assert.Null(empty);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void TryEnqueue_WithMaxEvents_EvictsTheOldestFilesAndReportsTheCount()
        {
            var directory = CreateTempDirectory();
            try
            {
                using var queue = new OfflineQueue(directory, maxEvents: 2, maxAgeHours: 72);
                var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
                var first = Guid.NewGuid();
                var second = Guid.NewGuid();
                var third = Guid.NewGuid();

                Enqueue(queue, first, baseTime);
                Enqueue(queue, second, baseTime.AddSeconds(1));

                Assert.True(
                    queue.TryEnqueue(Json, third, baseTime.AddSeconds(2), out var evicted, out var error),
                    error);

                Assert.Equal(1, evicted);
                Assert.Equal(2, queue.Count);
                Assert.Equal(1, queue.Dropped);

                // The newest two files survive.
                Assert.Equal(second, Peek(queue).EventId);
                queue.Remove(Peek(queue));
                Assert.Equal(third, Peek(queue).EventId);

                Assert.Equal(0, queue.EnforceLimits());
                Assert.Equal(1, queue.Dropped);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void EnforceLimits_WithMaxAgeHours_DeletesOldEntriesAndCountsThemAsDropped()
        {
            var directory = CreateTempDirectory();
            try
            {
                var oldTimestamp = DateTimeOffset.UtcNow.AddHours(-5);
                var oldId = Guid.NewGuid();

                // With the age limit disabled, the old event is kept.
                using (var writer = new OfflineQueue(directory, 500, maxAgeHours: 0))
                {
                    Enqueue(writer, oldId, oldTimestamp);
                    Assert.Equal(1, writer.Count);
                    Assert.Equal(0, writer.EnforceLimits());
                    Assert.Equal(1, writer.Count);
                }

                // A queue with a one-hour age limit removes it.
                using (var queue = new OfflineQueue(directory, 500, maxAgeHours: 1))
                {
                    Assert.Equal(1, queue.EnforceLimits());
                    Assert.Equal(0, queue.Count);
                    Assert.Equal(1, queue.Dropped);
                    Assert.False(queue.TryPeekOldest(out _));
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void TryEnqueue_WithAnExpiredTimestamp_IsEvictedImmediately()
        {
            var directory = CreateTempDirectory();
            try
            {
                using var queue = new OfflineQueue(directory, 500, maxAgeHours: 1);

                Assert.True(
                    queue.TryEnqueue(Json, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-2), out var evicted, out var error),
                    error);

                Assert.Equal(1, evicted);
                Assert.Equal(0, queue.Count);
                Assert.Equal(1, queue.Dropped);

                Enqueue(queue, Guid.NewGuid(), DateTimeOffset.UtcNow);
                Assert.Equal(1, queue.Count);
                Assert.Equal(1, queue.Dropped);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void Constructor_WhenAnotherQueueHoldsTheLock_IsUnavailableUntilItIsDisposed()
        {
            var directory = CreateTempDirectory();
            try
            {
                using (var first = new OfflineQueue(directory, 10, 1))
                {
                    Assert.True(first.IsAvailable, first.LastError);

                    using (var second = new OfflineQueue(directory, 10, 1))
                    {
                        Assert.False(second.IsAvailable);
                        Assert.NotNull(second.LastError);
                        Assert.Equal(0, second.Count);
                        Assert.Equal(0, second.Dropped);
                        Assert.False(second.TryEnqueue(Json, Guid.NewGuid(), DateTimeOffset.UtcNow, out var evicted, out var error));
                        Assert.Equal(0, evicted);
                        Assert.NotNull(error);
                        Assert.False(second.TryPeekOldest(out var entry));
                        Assert.Null(entry);
                        Assert.Equal(0, second.EnforceLimits());
                    }
                }

                using (var third = new OfflineQueue(directory, 10, 1))
                {
                    Assert.True(third.IsAvailable, third.LastError);
                    Enqueue(third, Guid.NewGuid(), DateTimeOffset.UtcNow);
                    Assert.Equal(1, third.Count);
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void Constructor_WithAStaleLockFileFromADeadProcess_AcquiresTheLock()
        {
            var directory = CreateTempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var lockPath = Path.Combine(directory, DirectoryLock.LockFileName);
                File.WriteAllText(lockPath, "999999");

                var queue = new OfflineQueue(directory, 10, 1);
                Assert.True(queue.IsAvailable, queue.LastError);
                queue.Dispose();

                // The stale content was replaced with this process id and the file
                // itself is left in place for the next queue.
                Assert.True(File.Exists(lockPath));
                Assert.Equal(
                    Process.GetCurrentProcess().Id.ToString(),
                    File.ReadAllText(lockPath));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void Constructor_WithInvalidLimits_ThrowsArgumentOutOfRangeException()
        {
            var directory = CreateTempDirectory();
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineQueue(directory, 0, 72));
                Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineQueue(directory, -1, 72));
                Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineQueue(directory, 500, -1));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void Constructor_WithABlankDirectory_IsUnavailableInsteadOfThrowing()
        {
            using var queue = new OfflineQueue("   ", 500, 72);

            Assert.False(queue.IsAvailable);
            Assert.NotNull(queue.LastError);
            Assert.Equal(0, queue.Count);
            Assert.False(queue.TryEnqueue(Json, EventId, DateTimeOffset.UtcNow, out var evicted, out var error));
            Assert.Equal(0, evicted);
            Assert.NotNull(error);
        }

        [Fact]
        public void OwnFiles_IgnoreTheLockFileTempFilesAndUnrelatedFiles()
        {
            var directory = CreateTempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "unrelated.json"), "{}");
                File.WriteAllText(Path.Combine(directory, "20261115T102231123Z-not-a-guid.json"), "{}");
                File.WriteAllText(Path.Combine(directory, "20261115T102231123Z-9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e.json.tmp-0123"), "{}");
                File.WriteAllText(Path.Combine(directory, "notatimestamp-9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e.json"), "{}");

                using var queue = new OfflineQueue(directory, 10, 1);
                Assert.Equal(0, queue.Count);
                Assert.Equal(0, queue.EnforceLimits());
                Assert.False(queue.TryPeekOldest(out _));

                Enqueue(queue, EventId, DateTimeOffset.UtcNow);
                Assert.Equal(1, queue.Count);

                // The queue must not delete unrelated files while enforcing limits.
                Assert.Equal(0, queue.EnforceLimits());
                Assert.True(File.Exists(Path.Combine(directory, "unrelated.json")));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void Dispose_IsIdempotentAndMakesLaterCallsNoOps()
        {
            var directory = CreateTempDirectory();
            try
            {
                var queue = new OfflineQueue(directory, 10, 1);
                Enqueue(queue, EventId, DateTimeOffset.UtcNow);
                var path = Peek(queue).Path;

                queue.Dispose();
                queue.Dispose();

                Assert.False(queue.IsAvailable);
                Assert.Equal(0, queue.Count);
                Assert.Equal(0, queue.Dropped);
                Assert.False(queue.TryEnqueue(Json, Guid.NewGuid(), DateTimeOffset.UtcNow, out var evicted, out var error));
                Assert.Equal(0, evicted);
                Assert.NotNull(error);
                Assert.False(queue.TryPeekOldest(out var entry));
                Assert.Null(entry);
                Assert.Equal(0, queue.EnforceLimits());

                queue.Remove(new OfflineQueueEntry(path, EventId, DateTimeOffset.UtcNow, Json));
                Assert.True(File.Exists(path));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void TryPeekOldest_SkipsAFileThatCannotBeRead()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // FileShare.None is only enforced by the Windows file system.
                return;
            }

            var directory = CreateTempDirectory();
            try
            {
                using var queue = new OfflineQueue(directory, 10, 1);
                var timestamp = DateTimeOffset.UtcNow;
                Enqueue(queue, EventId, timestamp);
                var path = Peek(queue).Path;

                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.False(queue.TryPeekOldest(out var blocked));
                    Assert.Null(blocked);
                }

                // Once the file is readable again the queue continues with it.
                Assert.True(queue.TryPeekOldest(out var entry));
                Assert.NotNull(entry);
                Assert.Equal(EventId, entry!.EventId);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static void Enqueue(OfflineQueue queue, Guid eventId, DateTimeOffset timestamp, string json = Json)
        {
            Assert.True(queue.TryEnqueue(json, eventId, timestamp, out _, out var error), error);
        }

        private static OfflineQueueEntry Peek(OfflineQueue queue)
        {
            Assert.True(queue.TryPeekOldest(out var entry));
            Assert.NotNull(entry);
            return entry!;
        }

        private static DateTime PeekTimestamp(OfflineQueue queue)
        {
            return Peek(queue).Timestamp.UtcDateTime;
        }

        private static string CreateTempDirectory()
        {
            return Path.Combine(
                Path.GetTempPath(),
                "glitchport-offline-queue-tests",
                Guid.NewGuid().ToString("N"));
        }

        private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
        {
            return new DateTimeOffset(
                value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond),
                value.Offset);
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
