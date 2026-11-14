using System;
using System.IO;
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

        [Fact]
        public async Task RunAsync_WithoutArguments_CapturesHelloThroughDefaultDsn()
        {
            await WithDsnAsync(null, async () =>
            {
                using var handler = new RecordingHttpMessageHandler();
                var result = await RunAsync(Array.Empty<string>(), handler);

                Assert.Equal(0, result.ExitCode);
                Assert.Equal(string.Empty, result.Stderr);

                Assert.Equal(HttpMethod.Post, handler.Method);
                Assert.NotNull(handler.RequestUri);
                Assert.Equal("http://localhost:5080/api/1/messages", handler.RequestUri!.ToString());
                Assert.Equal("dev-public-key", handler.ApiKey);

                Assert.Equal("hello", ParsePayload(handler).GetProperty("message").GetString());

                var eventId = Guid.Parse(ParsePayload(handler).GetProperty("event_id").GetString()!);
                Assert.Equal("event_id: " + eventId.ToString("D"), result.Stdout.TrimEnd('\r', '\n'));
            });
        }

        [Fact]
        public async Task RunAsync_WithMessageArgument_UsesDsnFromEnvironment()
        {
            await WithDsnAsync(CustomDsn, async () =>
            {
                using var handler = new RecordingHttpMessageHandler();
                var result = await RunAsync(new[] { "boom" }, handler);

                Assert.Equal(0, result.ExitCode);
                Assert.Equal(string.Empty, result.Stderr);

                Assert.NotNull(handler.RequestUri);
                Assert.Equal("http://relay.example.com:9000/api/7/messages", handler.RequestUri!.ToString());
                Assert.Equal("custom-key", handler.ApiKey);
                Assert.Equal("boom", ParsePayload(handler).GetProperty("message").GetString());
            });
        }

        [Fact]
        public async Task RunAsync_WhenRelayRejectsMessage_ReportsFlushFailureAndReturnsOne()
        {
            await WithDsnAsync(null, async () =>
            {
                using var handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError, "relay exploded");
                var result = await RunAsync(Array.Empty<string>(), handler);

                Assert.Equal(1, result.ExitCode);

                // Capture succeeds and prints the event id; the failure is reported by
                // the flush that follows.
                var stdoutLine = result.Stdout.TrimEnd('\r', '\n');
                Assert.StartsWith("event_id: ", stdoutLine, StringComparison.Ordinal);
                Assert.True(Guid.TryParse(stdoutLine.Substring("event_id: ".Length), out _));

                var errorLine = result.Stderr.TrimEnd('\r', '\n');
                Assert.StartsWith("error: relay http failed: ", errorLine, StringComparison.Ordinal);
                Assert.Contains("500", errorLine, StringComparison.Ordinal);
                Assert.DoesNotContain("\r", errorLine, StringComparison.Ordinal);
                Assert.DoesNotContain("\n", errorLine, StringComparison.Ordinal);
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

        private static async Task<RunResult> RunAsync(string[] args, RecordingHttpMessageHandler handler)
        {
            using var httpClient = new HttpClient(handler);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await HelloClientApp.RunAsync(
                args,
                stdout,
                stderr,
                CancellationToken.None,
                options => new GlitchPortClient(options, httpClient));

            return new RunResult(exitCode, stdout.ToString(), stderr.ToString());
        }

        private static JsonElement ParsePayload(RecordingHttpMessageHandler handler)
        {
            Assert.False(string.IsNullOrWhiteSpace(handler.Body));

            // Clone the root element so it stays valid after the document is
            // disposed; callers only read a couple of properties from it.
            using var document = JsonDocument.Parse(handler.Body!);
            return document.RootElement.Clone();
        }

        private static async Task WithDsnAsync(string? dsn, Func<Task> action)
        {
            var previous = Environment.GetEnvironmentVariable(HelloClientApp.DsnEnvironmentVariable);

            try
            {
                Environment.SetEnvironmentVariable(HelloClientApp.DsnEnvironmentVariable, dsn);
                await action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HelloClientApp.DsnEnvironmentVariable, previous);
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
