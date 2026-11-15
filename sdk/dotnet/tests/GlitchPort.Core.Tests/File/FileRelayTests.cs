using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class FileRelayTests
    {
        [Fact]
        public void RelayFile_ReturnsAFileDefinitionWithDefaults()
        {
            var definition = Relay.File();

            Assert.IsType<FileRelayDefinition>(definition);
            Assert.Equal("file", definition.Type);
            Assert.Equal("file", definition.Name);
            Assert.True(definition.Enabled);
            Assert.Equal(Level.Debug, definition.MinLevel);

            var options = Assert.IsType<FileRelayOptions>(definition.Options);
            Assert.Equal("logs/glitchport", options.Directory);
            Assert.Equal(FileFormat.Ndjson, options.Format);
            Assert.Equal(10, options.MaxFileSizeMb);
            Assert.Equal(7, options.MaxAgeDays);
            Assert.Equal(200, options.MaxTotalSizeMb);
            Assert.Equal(100, options.MaxFiles);
            Assert.Equal(1000, options.FlushIntervalMs);
            Assert.Equal(1000, options.QueueCapacity);
        }

        [Fact]
        public void RelayFile_ConfigureCallbackUpdatesTheOptions()
        {
            var definition = Relay.File(options =>
            {
                options.Name = "files";
                options.Enabled = false;
                options.MinLevel = Level.Error;
                options.Directory = "custom/logs";
                options.Format = FileFormat.Text;
                options.MaxFileSizeMb = 2;
                options.MaxAgeDays = 1;
                options.MaxTotalSizeMb = 3;
                options.MaxFiles = 4;
                options.FlushIntervalMs = 5;
            });

            Assert.Equal("files", definition.Name);
            Assert.False(definition.Enabled);
            Assert.Equal(Level.Error, definition.MinLevel);

            var options = Assert.IsType<FileRelayOptions>(definition.Options);
            Assert.Equal("custom/logs", options.Directory);
            Assert.Equal(FileFormat.Text, options.Format);
            Assert.Equal(2, options.MaxFileSizeMb);
            Assert.Equal(1, options.MaxAgeDays);
            Assert.Equal(3, options.MaxTotalSizeMb);
            Assert.Equal(4, options.MaxFiles);
            Assert.Equal(5, options.FlushIntervalMs);
        }

        [Fact]
        public void RelayFile_WhenDisabled_DoesNotCreateARelay()
        {
            var options = new GlitchPortOptions();
            options.Relays.Add(Relay.File(file => file.Enabled = false));

            // No enabled relay is left, so the client refuses to be created.
            Assert.Throws<ArgumentException>(() => new GlitchPortClient(options));
        }

        [Fact]
        public void RelayFile_WhenEnabled_CreatesAFileRelay()
        {
            var directory = CreateTempDirectory();

            try
            {
                var options = new GlitchPortOptions();
                options.Relays.Add(Relay.File(file =>
                {
                    file.Directory = directory;
                    file.Format = FileFormat.Text;
                    file.MinLevel = Level.Warning;
                }));

                using (var client = new GlitchPortClient(options))
                {
                    var relay = Assert.IsType<FileRelay>(Assert.Single(client.Relays));
                    Assert.Equal("file", relay.Name);
                    Assert.Equal("file", relay.Type);
                    Assert.Equal(Level.Warning, relay.MinLevel);
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void Constructor_WithNullOptions_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new FileRelay(null!));
        }

        [Fact]
        public void Constructor_CreatesTheDirectoryIncludingParents()
        {
            var root = CreateTempDirectory();

            try
            {
                var nested = Path.Combine(root, "a", "b", "c");

                using (var relay = new FileRelay(new FileRelayOptions { Directory = nested }))
                {
                    Assert.True(Directory.Exists(nested));
                }
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [Fact]
        public void Constructor_ResolvesRelativeDirectoriesAgainstTheCurrentDirectory()
        {
            var previousDirectory = Directory.GetCurrentDirectory();
            var root = CreateTempDirectory();

            try
            {
                Directory.SetCurrentDirectory(root);

                using (var relay = new FileRelay(new FileRelayOptions { Directory = Path.Combine("nested", "logs") }))
                {
                    Assert.True(Directory.Exists(Path.Combine(root, "nested", "logs")));
                }
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
                DeleteDirectory(root);
            }
        }

        [Fact]
        public void Constructor_WithDefaultOptions_CreatesLogsGlitchportUnderTheCurrentDirectory()
        {
            Assert.Equal("logs/glitchport", new FileRelayOptions().Directory);

            var previousDirectory = Directory.GetCurrentDirectory();
            var root = CreateTempDirectory();

            try
            {
                Directory.SetCurrentDirectory(root);

                using (var relay = new FileRelay(new FileRelayOptions()))
                {
                    Assert.True(Directory.Exists(Path.Combine(root, "logs", "glitchport")));
                }
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
                DeleteDirectory(root);
            }
        }

        [Fact]
        public async Task Handle_Ndjson_WritesTheRawJsonPlusANewline()
        {
            var directory = CreateTempDirectory();

            try
            {
                var captured = CreateEvent();

                using (var relay = new FileRelay(CreateOptions(directory)))
                {
                    Assert.True(relay.TryEnqueue(captured));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                var file = Assert.Single(Directory.GetFiles(directory, "glitchport-*"));
                Assert.Equal(captured.Json + "\n", File.ReadAllText(file));
                AssertNoByteOrderMark(file);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Handle_Text_WritesTheCompactFormattedLine()
        {
            var directory = CreateTempDirectory();

            try
            {
                var captured = CreateEvent(Level.Warning, "first line\nsecond line");

                using (var relay = new FileRelay(CreateOptions(directory, FileFormat.Text)))
                {
                    Assert.True(relay.TryEnqueue(captured));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                var file = Assert.Single(Directory.GetFiles(directory, "glitchport-*.log"));
                Assert.Equal(EventFormatter.FormatCompact(captured, color: false) + "\n", File.ReadAllText(file));
                AssertNoByteOrderMark(file);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Handle_Text_ExceptionEvent_WritesTheCompactExceptionLine()
        {
            var directory = CreateTempDirectory();

            try
            {
                var captured = CreateExceptionEvent();

                using (var relay = new FileRelay(CreateOptions(directory, FileFormat.Text)))
                {
                    Assert.True(relay.TryEnqueue(captured));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                var file = Assert.Single(Directory.GetFiles(directory, "glitchport-*.log"));
                var content = File.ReadAllText(file);

                // A self-contained literal: this test must catch a formatter
                // regression rather than echo EventFormatter.FormatCompact.
                Assert.Equal(
                    "2026-11-14 15:02:14.010 ERROR System.InvalidOperationException: boom  (id 3b7e0c11)\n",
                    content);

                // Text is the compact format: no frames, tags or metadata lines.
                Assert.DoesNotContain("at Game.", content, StringComparison.Ordinal);
                Assert.DoesNotContain("tags:", content, StringComparison.Ordinal);
                AssertNoByteOrderMark(file);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Handle_Ndjson_ExceptionEvent_WritesExactlyOneCompactV1JsonLine()
        {
            var directory = CreateTempDirectory();

            try
            {
                var captured = CreateExceptionEvent();

                using (var relay = new FileRelay(CreateOptions(directory)))
                {
                    Assert.True(relay.TryEnqueue(captured));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                var file = Assert.Single(Directory.GetFiles(directory, "glitchport-*.ndjson"));
                var content = File.ReadAllText(file);

                Assert.Equal(captured.Json + "\n", content);
                Assert.DoesNotContain('\n', captured.Json);
                Assert.Single(content.TrimEnd('\n').Split('\n'));

                using var document = JsonDocument.Parse(content.TrimEnd('\n'));
                var root = document.RootElement;

                Assert.Equal("error", root.GetProperty("level").GetString());
                Assert.Equal(
                    "System.InvalidOperationException",
                    root.GetProperty("exception").EnumerateArray().Single().GetProperty("type").GetString());
                Assert.Equal("star-raiders@1.2.3", root.GetProperty("release").GetString());
                Assert.Equal("production", root.GetProperty("environment").GetString());
                Assert.Equal("Stage1", root.GetProperty("tags").GetProperty("scene").GetString());
                AssertNoByteOrderMark(file);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Theory]
        [InlineData(FileFormat.Ndjson, ".ndjson")]
        [InlineData(FileFormat.Text, ".log")]
        public async Task FileName_UsesTheLocalTimestampTheProcessIdAndTheFormatExtension(
            FileFormat format,
            string extension)
        {
            var directory = CreateTempDirectory();

            try
            {
                var before = DateTime.Now.AddMinutes(-1);

                using (var relay = new FileRelay(CreateOptions(directory, format)))
                {
                    Assert.True(relay.TryEnqueue(CreateEvent()));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                var after = DateTime.Now.AddMinutes(1);
                var pid = Process.GetCurrentProcess().Id;
                var file = Assert.Single(Directory.GetFiles(directory, "glitchport-*" + extension));

                var match = Regex.Match(
                    Path.GetFileName(file),
                    "^glitchport-(\\d{8}-\\d{6})-" + pid.ToString(CultureInfo.InvariantCulture) + Regex.Escape(extension) + "$");

                Assert.True(match.Success, "Unexpected file name: " + Path.GetFileName(file));

                var timestamp = DateTime.ParseExact(
                    match.Groups[1].Value,
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None);

                Assert.InRange(timestamp, before, after);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Rotation_WhenTheFileExceedsTheSizeLimit_StartsANewFile()
        {
            var directory = CreateTempDirectory();

            try
            {
                var first = CreateEvent(json: "{\"n\":1,\"pad\":\"" + new string('a', 600 * 1024) + "\"}");
                var second = CreateEvent(json: "{\"n\":2,\"pad\":\"" + new string('b', 600 * 1024) + "\"}");
                var third = CreateEvent(json: "{\"n\":3}");

                var options = CreateOptions(directory);
                options.MaxFileSizeMb = 1;

                using (var relay = new FileRelay(options))
                {
                    Assert.True(relay.TryEnqueue(first));
                    Assert.True(relay.TryEnqueue(second));
                    Assert.True(relay.TryEnqueue(third));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                var contents = Directory.GetFiles(directory, "glitchport-*")
                    .Select(File.ReadAllText)
                    .ToArray();

                Assert.Equal(2, contents.Length);
                Assert.Contains(contents, content => content == first.Json + "\n" + second.Json + "\n");
                Assert.Contains(contents, content => content == third.Json + "\n");
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task FlushAsync_MakesBufferedWritesReadableFromDisk()
        {
            var directory = CreateTempDirectory();

            try
            {
                var options = CreateOptions(directory);

                // Long enough that only the explicit flush can make the event visible.
                options.FlushIntervalMs = 600000;

                using (var relay = new FileRelay(options))
                {
                    var captured = CreateEvent();

                    Assert.True(relay.TryEnqueue(captured));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);

                    // A plain read must succeed while the relay is still alive.
                    var file = Assert.Single(Directory.GetFiles(directory, "glitchport-*"));
                    Assert.Equal(captured.Json + "\n", File.ReadAllText(file));
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task FlushAsync_ThenMoreEvents_AppendsToTheSameFile()
        {
            var directory = CreateTempDirectory();

            try
            {
                var options = CreateOptions(directory);
                var first = CreateEvent();
                var second = CreateEvent(message: "second");

                using (var relay = new FileRelay(options))
                {
                    Assert.True(relay.TryEnqueue(first));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);

                    Assert.True(relay.TryEnqueue(second));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                var file = Assert.Single(Directory.GetFiles(directory, "glitchport-*"));
                Assert.Equal(first.Json + "\n" + second.Json + "\n", File.ReadAllText(file));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public void DisposeThroughTheRelayInterface_DrainsAndClosesTheActiveFile()
        {
            var directory = CreateTempDirectory();

            try
            {
                var relay = new FileRelay(CreateOptions(directory));
                var captured = CreateEvent();

                Assert.True(relay.TryEnqueue(captured));

                // GlitchPortClient holds its relays as IRelay, so that dispatch path
                // must drain the queued event and release the file handle.
                ((IRelay)relay).Dispose();

                var file = Assert.Single(Directory.GetFiles(directory, "glitchport-*"));
                Assert.Equal(captured.Json + "\n", File.ReadAllText(file));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task FlushInterval_FlushesBufferedWritesWithoutAFlushCall()
        {
            var directory = CreateTempDirectory();

            try
            {
                var options = CreateOptions(directory);
                options.FlushIntervalMs = 50;
                var captured = CreateEvent();

                using (var relay = new FileRelay(options))
                {
                    Assert.True(relay.TryEnqueue(captured));

                    WaitFor(
                        () =>
                        {
                            var files = Directory.GetFiles(directory, "glitchport-*");
                            return files.Length == 1 && ReadAllTextShared(files[0]) == captured.Json + "\n";
                        },
                        "the periodic flush to reach the disk");
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task ErrorLevelEvents_AreFlushedToDiskImmediately()
        {
            var directory = CreateTempDirectory();

            try
            {
                var options = CreateOptions(directory);

                // The timer never fires during the test; only the error level flush can.
                options.FlushIntervalMs = 600000;
                var captured = CreateEvent(Level.Error, "boom");

                using (var relay = new FileRelay(options))
                {
                    Assert.True(relay.TryEnqueue(captured));

                    WaitFor(
                        () =>
                        {
                            var files = Directory.GetFiles(directory, "glitchport-*");
                            return files.Length == 1 && ReadAllTextShared(files[0]) == captured.Json + "\n";
                        },
                        "the error event to be flushed");
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Retention_DeletesOnlyOwnedFilesOlderThanMaxAge()
        {
            var directory = CreateTempDirectory();

            try
            {
                var old = CreateFile(directory, "glitchport-20200101-000000-111.ndjson", 16, DateTime.UtcNow.AddDays(-30));
                var recent = CreateFile(directory, "glitchport-20200102-000000-222.ndjson", 16, DateTime.UtcNow.AddHours(-1));
                var foreignName = CreateFile(directory, "notes.ndjson", 16, DateTime.UtcNow.AddDays(-30));
                var foreignPrefix = CreateFile(directory, "glitchport-notes.ndjson", 16, DateTime.UtcNow.AddDays(-30));
                var foreignExtension = CreateFile(directory, "glitchport-20200101-000000-333.txt", 16, DateTime.UtcNow.AddDays(-30));

                var options = CreateOptions(directory);
                options.MaxAgeDays = 7;
                var captured = CreateEvent();

                using (var relay = new FileRelay(options))
                {
                    Assert.True(relay.TryEnqueue(captured));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                Assert.False(File.Exists(old));
                Assert.True(File.Exists(recent));
                Assert.True(File.Exists(foreignName));
                Assert.True(File.Exists(foreignPrefix));
                Assert.True(File.Exists(foreignExtension));

                var active = Assert.Single(
                    Directory.GetFiles(directory, "glitchport-*"),
                    file => File.ReadAllText(file) == captured.Json + "\n");
                Assert.True(File.Exists(active));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Retention_DeletesOldestFilesBeyondMaxFiles()
        {
            var directory = CreateTempDirectory();

            try
            {
                var oldest = CreateFile(directory, "glitchport-20200101-000001-1.ndjson", 16, DateTime.UtcNow.AddHours(-5));
                var older = CreateFile(directory, "glitchport-20200101-000002-2.ndjson", 16, DateTime.UtcNow.AddHours(-4));
                var middle = CreateFile(directory, "glitchport-20200101-000003-3.ndjson", 16, DateTime.UtcNow.AddHours(-3));
                var newer = CreateFile(directory, "glitchport-20200101-000004-4.ndjson", 16, DateTime.UtcNow.AddHours(-2));
                var newest = CreateFile(directory, "glitchport-20200101-000005-5.ndjson", 16, DateTime.UtcNow.AddHours(-1));

                var options = CreateOptions(directory);
                options.MaxFiles = 2;
                var captured = CreateEvent();

                using (var relay = new FileRelay(options))
                {
                    Assert.True(relay.TryEnqueue(captured));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                Assert.False(File.Exists(oldest));
                Assert.False(File.Exists(older));
                Assert.False(File.Exists(middle));
                Assert.False(File.Exists(newer));
                Assert.True(File.Exists(newest));

                var files = Directory.GetFiles(directory, "glitchport-*");
                Assert.Equal(2, files.Length);
                Assert.Contains(files, file => File.ReadAllText(file) == captured.Json + "\n");
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Retention_DeletesOldestFilesBeyondMaxTotalSize()
        {
            var directory = CreateTempDirectory();

            try
            {
                var oldest = CreateFile(directory, "glitchport-20200101-000001-1.ndjson", 1024 * 1024, DateTime.UtcNow.AddHours(-3));
                var middle = CreateFile(directory, "glitchport-20200101-000002-2.ndjson", 1024 * 1024, DateTime.UtcNow.AddHours(-2));
                var newest = CreateFile(directory, "glitchport-20200101-000003-3.ndjson", 1024 * 1024, DateTime.UtcNow.AddHours(-1));
                var foreign = CreateFile(directory, "huge-foreign-file.txt", 2 * 1024 * 1024, DateTime.UtcNow.AddDays(-30));

                var options = CreateOptions(directory);
                options.MaxTotalSizeMb = 2;
                var captured = CreateEvent();

                using (var relay = new FileRelay(options))
                {
                    Assert.True(relay.TryEnqueue(captured));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                Assert.False(File.Exists(oldest));
                Assert.False(File.Exists(middle));
                Assert.True(File.Exists(newest));
                Assert.True(File.Exists(foreign));

                var files = Directory.GetFiles(directory, "glitchport-*");
                Assert.Equal(2, files.Length);
                Assert.Contains(files, file => File.ReadAllText(file) == captured.Json + "\n");
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Retention_KeepsTheActiveFileEvenWhenTheTotalSizeLimitCannotBeMet()
        {
            var directory = CreateTempDirectory();

            try
            {
                var old = CreateFile(directory, "glitchport-20200101-000001-1.ndjson", 1024 * 1024, DateTime.UtcNow.AddDays(-1));

                var options = CreateOptions(directory);
                options.MaxTotalSizeMb = 1;

                // Larger than the whole budget: the old file has to go, but the
                // active file must survive even though its size alone exceeds it.
                var captured = CreateEvent(json: "{\"pad\":\"" + new string('x', 1536 * 1024) + "\"}");

                using (var relay = new FileRelay(options))
                {
                    Assert.True(relay.TryEnqueue(captured));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                Assert.False(File.Exists(old));

                var active = Assert.Single(Directory.GetFiles(directory, "glitchport-*"));
                Assert.Equal(captured.Json + "\n", File.ReadAllText(active));
                Assert.True(new FileInfo(active).Length > 1024 * 1024);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task Constructor_WhenTheDirectoryCannotBeCreated_DisablesTheRelay()
        {
            var root = CreateTempDirectory();

            try
            {
                var blocker = Path.Combine(root, "blocked");
                File.WriteAllText(blocker, "not a directory");

                using (var relay = new FileRelay(new FileRelayOptions { Directory = blocker }))
                {
                    Assert.False(relay.TryEnqueue(CreateEvent()));
                    Assert.False(relay.TryEnqueue(CreateEvent()));

                    var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                    Assert.Equal(0, result.Delivered);
                    Assert.Equal(2, result.Failed);
                    Assert.False(result.Succeeded);
                    Assert.False(string.IsNullOrWhiteSpace(result.LastError));
                }
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [Fact]
        public async Task Handle_WhenTheDirectoryDisappears_DisablesTheRelayAndCountsLaterEventsAsFailed()
        {
            var directory = CreateTempDirectory();

            try
            {
                using (var relay = new FileRelay(CreateOptions(directory)))
                {
                    Directory.Delete(directory, recursive: true);

                    Assert.True(relay.TryEnqueue(CreateEvent()));
                    var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                    Assert.Equal(0, result.Delivered);
                    Assert.True(result.Failed >= 1);
                    Assert.False(result.Succeeded);
                    Assert.False(string.IsNullOrWhiteSpace(result.LastError));

                    Assert.False(relay.TryEnqueue(CreateEvent()));
                    var after = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                    Assert.True(after.Failed >= result.Failed + 1);
                    Assert.False(string.IsNullOrWhiteSpace(after.LastError));
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task IoFailure_DoesNotPreventOtherRelaysFromDelivering()
        {
            var root = CreateTempDirectory();

            try
            {
                var blocker = Path.Combine(root, "blocked");
                File.WriteAllText(blocker, "not a directory");

                var fileRelay = new FileRelay(new FileRelayOptions { Directory = blocker });
                var memoryRelay = new FakeRelay("memory");
                var fanout = new RelayFanout(new IRelay[] { fileRelay, memoryRelay });

                try
                {
                    Assert.True(fanout.Enqueue(CreateEvent()));

                    var report = await fanout.FlushAsync(TimeSpan.FromSeconds(10));

                    Assert.Equal(2, report.Relays.Count);
                    Assert.Single(memoryRelay.Events);
                    Assert.False(report.AllSucceeded);
                    Assert.True(report.Relays.Single(result => result.Type == "file").Failed >= 1);
                    Assert.True(report.Relays.Single(result => result.Type == "fake").Succeeded);
                }
                finally
                {
                    fanout.Dispose();
                }
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [Fact]
        public async Task NewFiles_OnLinuxAndMacOs_AreOwnerReadWriteOnly()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            var directory = CreateTempDirectory();

            try
            {
                using (var relay = new FileRelay(CreateOptions(directory)))
                {
                    Assert.True(relay.TryEnqueue(CreateEvent()));
                    Assert.True((await relay.FlushAsync(TimeSpan.FromSeconds(10))).Succeeded);
                }

                var file = Assert.Single(Directory.GetFiles(directory, "glitchport-*"));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(file));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static FileRelayOptions CreateOptions(string directory, FileFormat format = FileFormat.Ndjson)
        {
            return new FileRelayOptions
            {
                Directory = directory,
                Format = format,
                FlushIntervalMs = 600000,
                MaxAgeDays = 0,
                MaxTotalSizeMb = 0,
                MaxFiles = 0,
            };
        }

        private static CapturedEvent CreateEvent(Level level = Level.Info, string message = "hello", string? json = null)
        {
            var eventId = Guid.Parse("3b7e0c11-1111-2222-3333-444455556666");
            var timestamp = new DateTimeOffset(2026, 11, 14, 6, 2, 14, 10, TimeSpan.Zero);

            return new CapturedEvent(
                eventId,
                timestamp,
                level,
                message,
                GlitchPortOptions.DefaultSdkName,
                "0.1.0",
                json ?? "{\"event_id\":\"" + eventId.ToString("D") + "\",\"message\":\"" + message + "\"}");
        }

        /// <summary>
        /// Builds a protocol v1 exception event, serialized by the production
        /// serializer so the NDJSON assertion covers real v1 JSON.
        /// </summary>
        private static CapturedEvent CreateExceptionEvent()
        {
            var eventId = Guid.Parse("3b7e0c11-1111-2222-3333-444455556666");

            // A local-wall-clock timestamp keeps the expected formatted line
            // independent of the machine timezone.
            var timestamp = new DateTimeOffset(new DateTime(2026, 11, 14, 15, 2, 14, 10, DateTimeKind.Local));

            var exception = new ExceptionInfo
            {
                Type = "System.InvalidOperationException",
                Value = "boom",
                Module = "Game",
            };
            exception.Frames.Add(new StackFrameInfo
            {
                Function = "Update",
                Module = "Game",
                FileName = "Update.cs",
                LineNumber = 12,
                InApp = true,
            });
            exception.Frames.Add(new StackFrameInfo
            {
                Function = "Start",
                Module = "Game",
                FileName = "Start.cs",
                LineNumber = 3,
                InApp = true,
            });

            var e = new GlitchPortEvent
            {
                EventId = eventId,
                Timestamp = timestamp,
                Level = Level.Error,
                Release = "star-raiders@1.2.3",
                Environment = "production",
            };
            e.Exceptions.Add(exception);
            e.Tags["scene"] = "Stage1";

            var json = EventSerializer.Serialize(e, GlitchPortOptions.DefaultSdkName, "0.1.0");

            return new CapturedEvent(
                eventId,
                timestamp,
                Level.Error,
                "System.InvalidOperationException: boom",
                GlitchPortOptions.DefaultSdkName,
                "0.1.0",
                json,
                new[]
                {
                    new CapturedExceptionSummary(
                        "System.InvalidOperationException",
                        "boom",
                        new[]
                        {
                            new CapturedStackFrame("Game", "Update", "Update.cs", 12),
                            new CapturedStackFrame("Game", "Start", "Start.cs", 3),
                        }),
                },
                new Dictionary<string, string>
                {
                    ["scene"] = "Stage1",
                },
                "star-raiders@1.2.3",
                "production");
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "glitchport-file-relay-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(path);
            return path;
        }

        private static string CreateFile(string directory, string name, int size, DateTime lastWriteTimeUtc)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', size).ToArray());
            File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
            return path;
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

        private static string ReadAllTextShared(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                return reader.ReadToEnd();
            }
        }

        private static void AssertNoByteOrderMark(string path)
        {
            var bytes = File.ReadAllBytes(path);

            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "The log file must not start with a UTF-8 byte order mark.");
        }

        private static void WaitFor(Func<bool> condition, string description)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (DateTime.UtcNow < deadline)
            {
                if (Evaluate(condition))
                {
                    return;
                }

                Thread.Sleep(20);
            }

            Assert.True(Evaluate(condition), "Timed out waiting for " + description + ".");
        }

        private static bool Evaluate(Func<bool> condition)
        {
            try
            {
                return condition();
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
