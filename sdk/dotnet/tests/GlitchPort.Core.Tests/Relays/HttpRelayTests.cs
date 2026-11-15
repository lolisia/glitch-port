using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class HttpRelayTests
    {
        private const string DsnString = "http://public-key@relay.example.com:5080/1";

        [Fact]
        public async Task Handle_PostsTheEventToTheEventsEndpoint()
        {
            using var handler = new TestHttpMessageHandler(
                (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.Accepted));
            using var httpClient = new HttpClient(handler);
            using var relay = new HttpRelay(Dsn.Parse(DsnString), new HttpRelayOptions(), httpClient);
            var captured = RelayTestData.CreateEvent();

            Assert.True(relay.TryEnqueue(captured));
            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.Delivered);
            Assert.Equal(1, handler.Attempts);

            Assert.Equal(HttpMethod.Post, handler.LastMethod);
            Assert.Equal("http://relay.example.com:5080/api/1/events", handler.LastRequestUri!.ToString());
            Assert.Equal("public-key", handler.LastApiKey);
            Assert.Equal("application/json; charset=utf-8", handler.LastContentType);
            Assert.Equal(captured.Json, handler.LastBody);
        }

        [Fact]
        public async Task Handle_WithASmallBody_SendsItUncompressed()
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var relay = new HttpRelay(Dsn.Parse(DsnString), new HttpRelayOptions(), httpClient);
            var captured = CreateEventWithBodyLength(HttpRelay.CompressionThresholdBytes - 1);

            Assert.True(relay.TryEnqueue(captured));
            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Succeeded);
            Assert.Null(handler.ContentEncoding);
            Assert.Equal(captured.Json, handler.Body);
            Assert.Equal(Encoding.UTF8.GetBytes(captured.Json), handler.BodyBytes);
        }

        [Theory]
        [InlineData(1024)]
        [InlineData(4096)]
        public async Task Handle_WithABodyOfAtLeast1024Bytes_SendsGzipCompressedContent(int bodyLength)
        {
            using var handler = new RecordingHttpMessageHandler();
            using var httpClient = new HttpClient(handler);
            using var relay = new HttpRelay(Dsn.Parse(DsnString), new HttpRelayOptions(), httpClient);
            var captured = CreateEventWithBodyLength(bodyLength);

            Assert.True(relay.TryEnqueue(captured));
            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Succeeded);
            Assert.Equal("gzip", handler.ContentEncoding);
            Assert.Equal(HttpMethod.Post, handler.Method);
            Assert.NotNull(handler.BodyBytes);

            using var compressed = new MemoryStream(handler.BodyBytes!);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Assert.Equal(captured.Json, reader.ReadToEnd());
        }

        [Fact]
        public async Task Handle_WithNonSuccessStatus_RetriesOnceThenRecordsTheStatus()
        {
            using var handler = new TestHttpMessageHandler(
                (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.InternalServerError, "relay exploded"));
            using var httpClient = new HttpClient(handler);
            using var relay = new HttpRelay(Dsn.Parse(DsnString), new HttpRelayOptions(), httpClient);

            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, handler.Attempts);
            Assert.Equal(0, result.Delivered);
            Assert.Equal(1, result.Failed);
            Assert.False(result.Succeeded);
            Assert.Contains("500", result.LastError, StringComparison.Ordinal);
            Assert.Contains("relay exploded", result.LastError, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Handle_WhenTheFirstAttemptFails_RetriesAndDelivers()
        {
            using var handler = new TestHttpMessageHandler(
                (attempt, _) => attempt == 1
                    ? TestHttpMessageHandler.Response(HttpStatusCode.ServiceUnavailable)
                    : TestHttpMessageHandler.Response(HttpStatusCode.OK));
            using var httpClient = new HttpClient(handler);
            using var relay = new HttpRelay(Dsn.Parse(DsnString), new HttpRelayOptions(), httpClient);

            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.Delivered);
            Assert.Equal(2, handler.Attempts);
        }

        [Fact]
        public async Task Handle_WhenTheNetworkThrows_RetriesOnceThenReportsFailed()
        {
            using var handler = new TestHttpMessageHandler(
                (_, _) => throw new HttpRequestException("network down"));
            using var httpClient = new HttpClient(handler);
            using var relay = new HttpRelay(Dsn.Parse(DsnString), new HttpRelayOptions(), httpClient);

            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, handler.Attempts);
            Assert.Equal(0, result.Delivered);
            Assert.Equal(1, result.Failed);
            Assert.False(result.Succeeded);
            Assert.Contains("network down", result.LastError, StringComparison.Ordinal);
        }

        [Fact]
        public void Constructor_UsesTheConfiguredOptions()
        {
            using var httpClient = new HttpClient(new TestHttpMessageHandler(
                (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.OK)));

            using var relay = new HttpRelay(
                Dsn.Parse(DsnString),
                new HttpRelayOptions { Name = "backup", MinLevel = Level.Error, QueueCapacity = 5 },
                httpClient);

            Assert.Equal("backup", relay.Name);
            Assert.Equal("http", relay.Type);
            Assert.Equal(Level.Error, relay.MinLevel);
        }

        [Fact]
        public async Task Handle_TruncatesALongResponseBodyInLastError()
        {
            var longBody = new string('x', 2000);
            using var handler = new TestHttpMessageHandler(
                (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.BadGateway, longBody));
            using var httpClient = new HttpClient(handler);
            using var relay = new HttpRelay(Dsn.Parse(DsnString), new HttpRelayOptions(), httpClient);

            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("502", result.LastError, StringComparison.Ordinal);
            Assert.True(result.LastError!.Length < longBody.Length);
        }

        [Fact]
        public async Task Handle_WithoutAnOfflineQueue_DoesNotQueueBacklog()
        {
            using var handler = new TestHttpMessageHandler(
                (_, _) => TestHttpMessageHandler.Response(HttpStatusCode.InternalServerError, "relay exploded"));
            using var httpClient = new HttpClient(handler);
            using var relay = new HttpRelay(Dsn.Parse(DsnString), new HttpRelayOptions(), httpClient);

            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            // The default options have no offline directory: a failed event is
            // retried once and reported as failed, never written to disk.
            Assert.Equal(2, handler.Attempts);
            Assert.Equal(0, result.Queued);
            Assert.Equal(0, result.Delivered);
            Assert.Equal(1, result.Failed);
            Assert.False(result.Succeeded);
        }

        /// <summary>
        /// Builds an event whose serialized body has exactly
        /// <paramref name="bodyLength"/> UTF-8 bytes, so the compression threshold
        /// can be tested precisely.
        /// </summary>
        private static CapturedEvent CreateEventWithBodyLength(int bodyLength)
        {
            const string prefix = "{\"message\":\"";
            const string suffix = "\"}";
            var json = prefix + new string('x', bodyLength - prefix.Length - suffix.Length) + suffix;
            Assert.Equal(bodyLength, Encoding.UTF8.GetByteCount(json));

            return new CapturedEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Level.Info,
                "hello",
                GlitchPortOptions.DefaultSdkName,
                "1.2.3",
                json);
        }
    }
}
