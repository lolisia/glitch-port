using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.HelloClient.Tests
{
    public sealed class HelloClientAppTests
    {
        private const string CustomDsn = "http://custom-key@relay.example.com:9000/7";
        private const string TerminalPathVariable = "GLITCHPORT_TERMINAL_PATH";

        public static IEnumerable<object[]> InvalidArguments()
        {
            yield return new object[] { new[] { "--level", "loud" } };
            yield return new object[] { new[] { "--level" } };
            yield return new object[] { new[] { "--count", "0" } };
            yield return new object[] { new[] { "--count", "-1" } };
            yield return new object[] { new[] { "--count", "many" } };
            yield return new object[] { new[] { "--count" } };
            yield return new object[] { new[] { "--hold", "-1" } };
            yield return new object[] { new[] { "--hold", "soon" } };
            yield return new object[] { new[] { "--hold" } };
            yield return new object[] { new[] { "--bogus" } };
            yield return new object[] { new[] { "-v" } };
            yield return new object[] { new[] { "hello", "again" } };
        }

        [Fact]
        public async Task RunAsync_WithoutArguments_CapturesHelloThroughDefaultDsn()
        {
            await WithDsnAsync(null, async () =>
            {
                using var handler = new RecordingHttpMessageHandler();
                var result = await RunAsync(Array.Empty<string>(), handler);

                Assert.Equal(0, result.ExitCode);
                Assert.Equal(string.Empty, result.Stderr);

                var request = Assert.Single(handler.Requests);
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.NotNull(request.RequestUri);
                Assert.Equal("http://localhost:5080/api/1/events", request.RequestUri!.ToString());
                Assert.Equal("dev-public-key", request.ApiKey);

                var payload = ParsePayload(request.Body);
                Assert.Equal("hello", payload.GetProperty("message").GetString());
                Assert.Equal("info", payload.GetProperty("level").GetString());

                var lines = SplitLines(result.Stdout);
                Assert.Equal(2, lines.Length);
                Assert.StartsWith("event_id: ", lines[0], StringComparison.Ordinal);
                Assert.True(Guid.TryParse(lines[0].Substring("event_id: ".Length), out _));
                Assert.Equal("relay http (http): delivered=1 failed=0 dropped=0", lines[1]);
            });
        }

        [Fact]
        public async Task RunAsync_WithEnvironmentDsn_UsesEveryConfiguredRelay()
        {
            var directory = CreateTempDirectory();

            try
            {
                var dsn = CustomDsn
                    + ";file:?dir=" + Uri.EscapeDataString(directory) + "&format=ndjson";

                await WithDsnAsync(dsn, async () =>
                {
                    using var handler = new RecordingHttpMessageHandler();
                    var result = await RunAsync(new[] { "boom" }, handler);

                    Assert.Equal(0, result.ExitCode);
                    Assert.Equal(string.Empty, result.Stderr);

                    var request = Assert.Single(handler.Requests);
                    Assert.NotNull(request.RequestUri);
                    Assert.Equal("http://relay.example.com:9000/api/7/events", request.RequestUri!.ToString());
                    Assert.Equal("custom-key", request.ApiKey);
                    Assert.Equal("boom", ParsePayload(request.Body).GetProperty("message").GetString());

                    var lines = SplitLines(result.Stdout);
                    Assert.Contains("relay http (http): delivered=1 failed=0 dropped=0", lines);
                    Assert.Contains("relay file (file): delivered=1 failed=0 dropped=0", lines);

                    var file = Assert.Single(Directory.GetFiles(directory, "*.ndjson"));
                    var content = File.ReadAllText(file);
                    Assert.Contains("\"message\":\"boom\"", content, StringComparison.Ordinal);
                });
            }
            finally
            {
                TryDeleteDirectory(directory);
            }
        }

        [Theory]
        [InlineData("warning")]
        [InlineData("WARNING")]
        [InlineData("Fatal")]
        public async Task RunAsync_WithLevel_SendsTheParsedLevel(string value)
        {
            await WithDsnAsync(null, async () =>
            {
                using var handler = new RecordingHttpMessageHandler();
                var result = await RunAsync(new[] { "hello", "--level", value }, handler);

                Assert.Equal(0, result.ExitCode);
                Assert.Equal(string.Empty, result.Stderr);

                var request = Assert.Single(handler.Requests);
                Assert.Equal(
                    value.ToLowerInvariant(),
                    ParsePayload(request.Body).GetProperty("level").GetString());
            });
        }

        [Fact]
        public async Task RunAsync_WithCount_CapturesTheMessageThreeTimes()
        {
            await WithDsnAsync(null, async () =>
            {
                using var handler = new RecordingHttpMessageHandler();
                var result = await RunAsync(new[] { "hello", "--count", "3" }, handler);

                Assert.Equal(0, result.ExitCode);
                Assert.Equal(string.Empty, result.Stderr);
                Assert.Equal(3, handler.Requests.Count);

                var lines = SplitLines(result.Stdout);
                var eventIds = lines
                    .Where(line => line.StartsWith("event_id: ", StringComparison.Ordinal))
                    .ToArray();

                Assert.Equal(3, eventIds.Length);
                foreach (var line in eventIds)
                {
                    Assert.True(Guid.TryParse(line.Substring("event_id: ".Length), out _));
                }

                Assert.Equal(3, eventIds.Distinct(StringComparer.Ordinal).Count());
                Assert.Contains("relay http (http): delivered=3 failed=0 dropped=0", lines);
            });
        }

        [Fact]
        public async Task RunAsync_WhenRelayRejectsMessage_ReportsTheFailureAndReturnsOne()
        {
            await WithDsnAsync(null, async () =>
            {
                using var handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError, "relay exploded");
                var result = await RunAsync(Array.Empty<string>(), handler);

                Assert.Equal(1, result.ExitCode);
                Assert.Equal(string.Empty, result.Stderr);

                // Capture succeeds and prints the event id; the failure is reported by
                // the flush that follows.
                var lines = SplitLines(result.Stdout);
                Assert.StartsWith("event_id: ", lines[0], StringComparison.Ordinal);
                Assert.True(Guid.TryParse(lines[0].Substring("event_id: ".Length), out _));
                Assert.Contains("relay http (http): delivered=0 failed=1 dropped=0", lines);
            });
        }

        [Theory]
        [MemberData(nameof(InvalidArguments))]
        public async Task RunAsync_WithInvalidArguments_WritesOneErrorLineAndReturnsOne(string[] args)
        {
            await WithDsnAsync(null, async () =>
            {
                using var handler = new RecordingHttpMessageHandler();
                var result = await RunAsync(args, handler);

                Assert.Equal(1, result.ExitCode);
                Assert.Equal(string.Empty, result.Stdout);
                Assert.Empty(handler.Requests);

                var line = Assert.Single(SplitLines(result.Stderr));
                Assert.StartsWith("error: ", line, StringComparison.Ordinal);
                Assert.True(line.Length > "error: ".Length, "the error line carried no message.");
            });
        }

        [Fact]
        public async Task RunAsync_WithTerminalRelay_PrintsTheChildProcessId()
        {
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            Assert.True(
                File.Exists(Path.Combine(AppContext.BaseDirectory, "glitchport-terminal" + suffix)),
                "glitchport-terminal was not copied next to the test assembly.");

            // Ignore a machine-wide override so the renderer shipped next to the test
            // assembly is the one that gets started.
            await WithEnvironmentAsync(TerminalPathVariable, null, async () =>
            {
                await WithDsnAsync("terminal:?window=inherit&format=compact", async () =>
                {
                    using var handler = new RecordingHttpMessageHandler();
                    var result = await RunAsync(Array.Empty<string>(), handler);

                    Assert.Equal(0, result.ExitCode);
                    Assert.Equal(string.Empty, result.Stderr);
                    Assert.Empty(handler.Requests);

                    var lines = SplitLines(result.Stdout);
                    Assert.Contains(
                        lines,
                        line => line.StartsWith("terminal_pid: ", StringComparison.Ordinal)
                            && int.TryParse(
                                line.Substring("terminal_pid: ".Length),
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out var processId)
                            && processId > 0);
                    Assert.Contains("relay terminal (terminal): delivered=1 failed=0 dropped=0", lines);
                });
            });
        }

        [Fact]
        public async Task RunAsync_WithHold_WaitsUntilCancelled()
        {
            await WithDsnAsync(null, async () =>
            {
                using var handler = new RecordingHttpMessageHandler();
                using var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(TimeSpan.FromSeconds(1));

                var started = DateTime.UtcNow;
                var result = await RunAsync(new[] { "--hold", "60" }, handler, cancellation.Token);
                var elapsed = DateTime.UtcNow - started;

                Assert.Equal(1, result.ExitCode);
                Assert.True(
                    elapsed < TimeSpan.FromSeconds(30),
                    "the hold did not observe the cancellation token.");
                Assert.Contains("event_id: ", result.Stdout, StringComparison.Ordinal);
                Assert.StartsWith("error: ", result.Stderr, StringComparison.Ordinal);
                Assert.Single(SplitLines(result.Stderr));
            });
        }

        [Fact]
        public async Task RunAsync_WhenDsnIsInvalid_WritesErrorAndDoesNotSend()
        {
            await WithDsnAsync("not a dsn", async () =>
            {
                using var handler = new RecordingHttpMessageHandler();
                var result = await RunAsync(Array.Empty<string>(), handler);

                Assert.Equal(1, result.ExitCode);
                Assert.Equal(string.Empty, result.Stdout);
                Assert.StartsWith("error: ", result.Stderr, StringComparison.Ordinal);
                Assert.Null(handler.RequestUri);
            });
        }

        [Fact]
        public async Task RunAsync_WhenExceptionMessageSpansLines_FoldsItIntoSingleLine()
        {
            await WithDsnAsync(null, async () =>
            {
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();

                var exitCode = await HelloClientApp.RunAsync(
                    Array.Empty<string>(),
                    stdout,
                    stderr,
                    CancellationToken.None,
                    _ => throw new InvalidOperationException("line one\nline two\r\nline three"));

                Assert.Equal(1, exitCode);
                Assert.Equal(string.Empty, stdout.ToString());
                Assert.Equal(
                    "error: line one line two  line three",
                    stderr.ToString().TrimEnd('\r', '\n'));
            });
        }

        [Fact]
        public async Task RunAsync_WhenCancelled_ReportsErrorAndReturnsOne()
        {
            await WithDsnAsync(null, async () =>
            {
                using var handler = new RecordingHttpMessageHandler();
                using var httpClient = new HttpClient(handler);
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                var exitCode = await HelloClientApp.RunAsync(
                    Array.Empty<string>(),
                    stdout,
                    stderr,
                    cancellation.Token,
                    options => new GlitchPortClient(options, httpClient));

                Assert.Equal(1, exitCode);
                Assert.Equal(string.Empty, stdout.ToString());
                Assert.StartsWith("error: ", stderr.ToString(), StringComparison.Ordinal);
                Assert.Null(handler.RequestUri);
            });
        }

        [Fact]
        public async Task ResolveDsn_FallsBackToDefaultOnlyWhenVariableIsMissingOrBlank()
        {
            await WithDsnAsync(null, () =>
            {
                Assert.Equal(HelloClientApp.DefaultDsn, HelloClientApp.ResolveDsn());
                return Task.CompletedTask;
            });

            await WithDsnAsync("   ", () =>
            {
                Assert.Equal(HelloClientApp.DefaultDsn, HelloClientApp.ResolveDsn());
                return Task.CompletedTask;
            });

            await WithDsnAsync(CustomDsn, () =>
            {
                Assert.Equal(CustomDsn, HelloClientApp.ResolveDsn());
                return Task.CompletedTask;
            });
        }

        private static async Task<RunResult> RunAsync(
            string[] args,
            RecordingHttpMessageHandler handler,
            CancellationToken cancellationToken = default)
        {
            using var httpClient = new HttpClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await HelloClientApp.RunAsync(
                args,
                stdout,
                stderr,
                cancellationToken,
                options => new GlitchPortClient(options, httpClient));

            return new RunResult(exitCode, stdout.ToString(), stderr.ToString());
        }

        private static JsonElement ParsePayload(string? body)
        {
            Assert.False(string.IsNullOrWhiteSpace(body));

            // Clone the root element so it stays valid after the document is
            // disposed; callers only read a couple of properties from it.
            using var document = JsonDocument.Parse(body!);
            return document.RootElement.Clone();
        }

        private static string[] SplitLines(string value)
        {
            return value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "glitchport-hello-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception)
            {
                // Leftovers in the temp directory are harmless.
            }
        }

        private static Task WithDsnAsync(string? dsn, Func<Task> action)
        {
            return WithEnvironmentAsync(HelloClientApp.DsnEnvironmentVariable, dsn, action);
        }

        private static async Task WithEnvironmentAsync(string name, string? value, Func<Task> action)
        {
            var previous = Environment.GetEnvironmentVariable(name);

            try
            {
                Environment.SetEnvironmentVariable(name, value);
                await action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }

        private sealed class RunResult
        {
            public RunResult(int exitCode, string stdout, string stderr)
            {
                ExitCode = exitCode;
                Stdout = stdout;
                Stderr = stderr;
            }

            public int ExitCode { get; }

            public string Stdout { get; }

            public string Stderr { get; }
        }
    }
}
