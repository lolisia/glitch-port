using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class GlitchPortClientTests
    {
        private const string ValidDsn = "http://public-key@relay.example.com:5080/1";
        private const string ExpectedContentType = "application/json; charset=utf-8";

        [Fact]
        public async Task CaptureMessageAsync_PostsExpectedRequestAndReturnsEventId()
        {
            using var handler = new RecordingHttpMessageHandler(HttpStatusCode.Accepted, "{\"id\":\"ignored\"}");
            using var httpClient = new HttpClient(handler);
            using var client = CreateClient(httpClient);
            var before = DateTimeOffset.UtcNow;

            var eventId = await client.CaptureMessageAsync("hello");
            var report = await client.FlushAsync();

            Assert.NotEqual(Guid.Empty, eventId);
            Assert.True(report.AllSucceeded);
            var relayResult = Assert.Single(report.Relays);
            Assert.Equal("http", relayResult.Name);
            Assert.Equal(1, relayResult.Delivered);
            Assert.Equal(0, relayResult.Failed);
            Assert.Equal(0, relayResult.Dropped);

            Assert.Equal(HttpMethod.Post, handler.Method);

            Assert.NotNull(handler.RequestUri);
            var requestUri = handler.RequestUri!;
            Assert.Equal("http", requestUri.Scheme);
            Assert.Equal("relay.example.com", requestUri.Host);
            Assert.Equal(5080, requestUri.Port);
            Assert.Equal("/api/1/messages", requestUri.AbsolutePath);

            Assert.Equal("public-key", handler.ApiKey);
            Assert.Equal(ExpectedContentType, handler.ContentType);

            Assert.NotNull(handler.Body);
            using var document = JsonDocument.Parse(handler.Body!);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("event_id", out var eventIdElement));
            Assert.Equal(eventId, Guid.Parse(eventIdElement.GetString()!));

            Assert.True(root.TryGetProperty("timestamp", out var timestampElement));
            var timestamp = DateTimeOffset.Parse(
                timestampElement.GetString()!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            Assert.Equal(TimeSpan.Zero, timestamp.Offset);
            Assert.True(timestamp >= before.AddSeconds(-1));
            Assert.True(timestamp <= DateTimeOffset.UtcNow.AddSeconds(1));

            Assert.True(root.TryGetProperty("message", out var messageElement));
            Assert.Equal("hello", messageElement.GetString());

            Assert.True(root.TryGetProperty("sdk", out var sdkElement));
            Assert.Equal(GlitchPortOptions.DefaultSdkName, sdkElement.GetProperty("name").GetString());
            Assert.False(string.IsNullOrWhiteSpace(sdkElement.GetProperty("version").GetString()));
        }

        [Fact]
        public async Task CaptureMessageAsync_WithLevelOverload_WritesLevelToPayload()
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = CreateClient(httpClient);

            await client.CaptureMessageAsync("boom", Level.Error);
            await client.FlushAsync();

            using var document = JsonDocument.Parse(handler.Body!);
            Assert.Equal("error", document.RootElement.GetProperty("level").GetString());
        }

        [Fact]
        public async Task CaptureMessageAsync_EscapesJsonSpecialCharactersAndKeepsUnicode()
        {
            var message = "quote:\" backslash:\\ newline:\n tab:\t backspace:\b formfeed:\f return:\r control:\u0001 emoji:\U0001F3AE";
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = CreateClient(httpClient);

            await client.CaptureMessageAsync(message);
            await client.FlushAsync();

            Assert.NotNull(handler.Body);
            var body = handler.Body!;

            // The raw payload must not contain literal control characters, and every
            // special character must use the expected JSON escape.
            Assert.False(body.Contains('\n'));
            Assert.False(body.Contains('\t'));
            Assert.False(body.Contains('\r'));
            Assert.Contains("\\\"", body);
            Assert.Contains("\\\\", body);
            Assert.Contains("\\n", body);
            Assert.Contains("\\t", body);
            Assert.Contains("\\b", body);
            Assert.Contains("\\f", body);
            Assert.Contains("\\r", body);
            Assert.Contains("\\u0001", body);

            // Non-ASCII text stays raw (UTF-8), it is not escaped.
            Assert.Contains("\U0001F3AE", body);

            using var document = JsonDocument.Parse(body);
            Assert.Equal(message, document.RootElement.GetProperty("message").GetString());
        }

        [Fact]
        public async Task CaptureMessageAsync_WhenSdkVersionIsNull_UsesAssemblyInformationalVersion()
        {
            var expectedVersion = typeof(GlitchPortClient).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            Assert.False(string.IsNullOrWhiteSpace(expectedVersion));

            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = CreateClient(httpClient);

            await client.CaptureMessageAsync("hello");
            await client.FlushAsync();

            using var document = JsonDocument.Parse(handler.Body!);
            Assert.Equal(expectedVersion, document.RootElement.GetProperty("sdk").GetProperty("version").GetString());
        }

        [Fact]
        public async Task CaptureMessageAsync_UsesConfiguredSdkNameAndVersion()
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = new GlitchPortClient(
                new GlitchPortOptions
                {
                    Dsn = Dsn.Parse(ValidDsn),
                    SdkName = "custom.sdk",
                    SdkVersion = "9.8.7",
                },
                httpClient);

            await client.CaptureMessageAsync("hello");
            await client.FlushAsync();

            using var document = JsonDocument.Parse(handler.Body!);
            var sdk = document.RootElement.GetProperty("sdk");
            Assert.Equal("custom.sdk", sdk.GetProperty("name").GetString());
            Assert.Equal("9.8.7", sdk.GetProperty("version").GetString());
        }

        [Fact]
        public async Task CaptureMessageAsync_WithPortlessDsn_PostsToDefaultPort()
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = new GlitchPortClient(
                new GlitchPortOptions { Dsn = Dsn.Parse("http://public-key@relay.example.com/9") },
                httpClient);

            await client.CaptureMessageAsync("hello");
            await client.FlushAsync();

            Assert.NotNull(handler.RequestUri);
            Assert.Equal(80, handler.RequestUri!.Port);
            Assert.Equal("/api/9/messages", handler.RequestUri.AbsolutePath);
        }

        [Fact]
        public async Task CaptureMessageAsync_NonSuccessStatus_ReportsFailureInFlushReport()
        {
            using var handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError, "relay exploded");
            using var httpClient = new HttpClient(handler);
            using var client = CreateClient(httpClient);

            var eventId = await client.CaptureMessageAsync("hello");

            Assert.NotEqual(Guid.Empty, eventId);

            var report = await client.FlushAsync();
            var result = Assert.Single(report.Relays);

            Assert.Equal("http", result.Name);
            Assert.Equal(0, result.Delivered);
            Assert.Equal(1, result.Failed);
            Assert.Equal(0, result.Dropped);
            Assert.False(result.Succeeded);
            Assert.False(report.AllSucceeded);
            Assert.Contains("500", result.LastError, StringComparison.Ordinal);
            Assert.Contains("relay exploded", result.LastError, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CaptureMessageAsync_PreCancelledToken_ThrowsAndDoesNotSend()
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = CreateClient(httpClient);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.CaptureMessageAsync("hello", cancellation.Token));

            Assert.Null(handler.Body);
        }

        [Fact]
        public async Task CaptureMessageAsync_ReturnsBeforeTheRequestCompletes()
        {
            using var handler = new BlockingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = CreateClient(httpClient);

            var capture = client.CaptureMessageAsync("hello");
            var winner = await Task.WhenAny(capture, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(capture, winner);
            Assert.NotEqual(Guid.Empty, await capture);

            await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, handler.Attempts);

            handler.Release();

            var report = await client.FlushAsync();
            Assert.True(report.AllSucceeded);
            Assert.Equal(1, Assert.Single(report.Relays).Delivered);
        }

        [Fact]
        public async Task CaptureMessageAsync_WithHigherMinLevel_DoesNotReachTheRelay()
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = new GlitchPortClient(
                new GlitchPortOptions
                {
                    Relays =
                    {
                        Relay.Http(ValidDsn, options => options.MinLevel = Level.Error),
                    },
                },
                httpClient);

            await client.CaptureMessageAsync("info only");
            await client.CaptureMessageAsync("boom", Level.Error);

            var report = await client.FlushAsync();

            Assert.True(report.AllSucceeded);
            Assert.Equal(1, Assert.Single(report.Relays).Delivered);

            using var document = JsonDocument.Parse(handler.Body!);
            Assert.Equal("boom", document.RootElement.GetProperty("message").GetString());
        }

        [Fact]
        public async Task Dispose_FlushesPendingEvents()
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient);

            await client.CaptureMessageAsync("hello");
            client.Dispose();

            Assert.NotNull(handler.Body);
            using var document = JsonDocument.Parse(handler.Body!);
            Assert.Equal("hello", document.RootElement.GetProperty("message").GetString());
        }

        [Fact]
        public async Task Dispose_ReturnsEvenWhenARelayIsStalled()
        {
            using var handler = new BlockingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var client = CreateClient(httpClient);

            await client.CaptureMessageAsync("hello");
            await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));

            var stopwatch = Stopwatch.StartNew();
            client.Dispose();
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "Dispose blocked for " + stopwatch.Elapsed + ".");

            handler.Release();
        }

        [Fact]
        public async Task Dispose_DoesNotDisposeInjectedHttpClient()
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            var client = new GlitchPortClient(new GlitchPortOptions { Dsn = Dsn.Parse(ValidDsn) }, httpClient);

            client.Dispose();

            Assert.False(handler.Disposed);

            // The injected HttpClient must still be usable after the SDK client is disposed.
            using var response = await httpClient.GetAsync("http://relay.example.com:5080/healthz");
            Assert.True(response.IsSuccessStatusCode);
        }

        [Fact]
        public void Dispose_WithoutInjectedHttpClient_IsIdempotent()
        {
            var client = new GlitchPortClient(new GlitchPortOptions { Dsn = Dsn.Parse(ValidDsn) });

            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void Constructor_WithoutOptions_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new GlitchPortClient(null!));
        }

        [Fact]
        public void Constructor_WithoutRelays_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new GlitchPortClient(new GlitchPortOptions()));
        }

        [Fact]
        public void Constructor_WithOnlyDisabledRelays_ThrowsArgumentException()
        {
            var options = new GlitchPortOptions();
            options.Relays.Add(Relay.Http(ValidDsn, relayOptions => relayOptions.Enabled = false));

            Assert.Throws<ArgumentException>(() => new GlitchPortClient(options));
        }

        [Fact]
        public void Constructor_ExposesConfiguredRelaysInOrder()
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = new GlitchPortClient(
                new GlitchPortOptions
                {
                    Dsn = Dsn.Parse(ValidDsn),
                    Relays =
                    {
                        Relay.Http("http://other-key@relay.example.com:5080/2", options => options.Name = "backup"),
                        Relay.Http("http://third-key@relay.example.com:5080/3", options => options.Enabled = false),
                    },
                },
                httpClient);

            Assert.Equal(2, client.Relays.Count);
            Assert.Equal("http", client.Relays[0].Name);
            Assert.Equal("http", client.Relays[0].Type);
            Assert.Equal("backup", client.Relays[1].Name);
            Assert.Equal("http", client.Relays[1].Type);
        }

        private static GlitchPortClient CreateClient(HttpClient httpClient)
        {
            return new GlitchPortClient(new GlitchPortOptions { Dsn = Dsn.Parse(ValidDsn) }, httpClient);
        }
    }
}
