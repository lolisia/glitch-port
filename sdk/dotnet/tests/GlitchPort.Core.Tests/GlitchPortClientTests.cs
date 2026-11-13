using System;
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

            Assert.NotEqual(Guid.Empty, eventId);
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
        public async Task CaptureMessageAsync_EscapesJsonSpecialCharactersAndKeepsUnicode()
        {
            var message = "quote:\" backslash:\\ newline:\n tab:\t backspace:\b formfeed:\f return:\r control:\u0001 emoji:\U0001F3AE";
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var client = CreateClient(httpClient);

            await client.CaptureMessageAsync(message);

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

            Assert.NotNull(handler.RequestUri);
            Assert.Equal(80, handler.RequestUri!.Port);
            Assert.Equal("/api/9/messages", handler.RequestUri.AbsolutePath);
        }

        [Fact]
        public async Task CaptureMessageAsync_NonSuccessStatus_ThrowsGlitchPortException()
        {
            using var handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError, "relay exploded");
            using var httpClient = new HttpClient(handler);
            using var client = CreateClient(httpClient);

            var exception = await Assert.ThrowsAsync<GlitchPortException>(
                () => client.CaptureMessageAsync("hello"));

            Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
            Assert.Equal("relay exploded", exception.ResponseBody);
            Assert.NotNull(exception.RequestUri);
            Assert.Equal("/api/1/messages", exception.RequestUri!.AbsolutePath);
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
        public void Constructor_WithoutDsn_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new GlitchPortClient(new GlitchPortOptions()));
        }

        private static GlitchPortClient CreateClient(HttpClient httpClient)
        {
            return new GlitchPortClient(new GlitchPortOptions { Dsn = Dsn.Parse(ValidDsn) }, httpClient);
        }
    }
}
