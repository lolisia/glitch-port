using System.Net;
using System.Text.Json;
using GlitchPort.Collector.Contracts;
using Xunit;

namespace GlitchPort.Transport.Http.Tests;

public sealed class HttpEnvelopePublisherTests
{
    private static readonly string[] ExpectedPropertyNames =
    [
        "version",
        "event_id",
        "project_id",
        "received_at",
        "endpoint",
        "message",
        "client_timestamp",
        "sdk_name",
        "sdk_version",
        "payload",
        "client_ip",
    ];

    [Fact]
    public async Task PublishAsync_RotatesStartEndpointAcrossPublishes()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var publisher = TestData.CreatePublisher(
            handler,
            TestData.CreateOptions("https://relay-a.test", "https://relay-b.test"));

        await publisher.PublishAsync([TestData.CreateEnvelope()], CancellationToken.None);
        await publisher.PublishAsync([TestData.CreateEnvelope()], CancellationToken.None);
        await publisher.PublishAsync([TestData.CreateEnvelope()], CancellationToken.None);

        var hosts = handler.Requests.Select(request => request.Uri.Host).ToArray();
        Assert.Equal(new[] { "relay-a.test", "relay-b.test", "relay-a.test" }, hosts);
    }

    [Fact]
    public async Task PublishAsync_RetriesNextEndpointAfterServerError()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            var statusCode = request.RequestUri!.Host == "relay-a.test"
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK;

            return Task.FromResult(new HttpResponseMessage(statusCode));
        });

        var publisher = TestData.CreatePublisher(
            handler,
            TestData.CreateOptions("https://relay-a.test", "https://relay-b.test"));

        await publisher.PublishAsync([TestData.CreateEnvelope()], CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("relay-a.test", handler.Requests[0].Uri.Host);
        Assert.Equal("relay-b.test", handler.Requests[1].Uri.Host);
        Assert.Equal("https://relay-b.test/internal/envelopes", handler.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task PublishAsync_ThrowsEnvelopePublishExceptionWhenAllEndpointsFail()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var publisher = TestData.CreatePublisher(
            handler,
            TestData.CreateOptions("https://relay-a.test", "https://relay-b.test"));

        var exception = await Assert.ThrowsAsync<EnvelopePublishException>(
            () => publisher.PublishAsync([TestData.CreateEnvelope()], CancellationToken.None).AsTask());

        Assert.Equal(
            new[] { "https://relay-a.test", "https://relay-b.test" },
            exception.AttemptedEndpoints);
        Assert.NotNull(exception.InnerException);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PublishAsync_ThrowsEnvelopePublishExceptionWithoutRetryingOnClientError()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var publisher = TestData.CreatePublisher(
            handler,
            TestData.CreateOptions("https://relay-a.test", "https://relay-b.test"));

        var exception = await Assert.ThrowsAsync<EnvelopePublishException>(
            () => publisher.PublishAsync([TestData.CreateEnvelope()], CancellationToken.None).AsTask());

        Assert.Equal(new[] { "https://relay-a.test" }, exception.AttemptedEndpoints);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PublishAsync_RetriesNextEndpointAfterConnectionError()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
            request.RequestUri!.Host == "relay-a.test"
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var publisher = TestData.CreatePublisher(
            handler,
            TestData.CreateOptions("https://relay-a.test", "https://relay-b.test"));

        await publisher.PublishAsync([TestData.CreateEnvelope()], CancellationToken.None);

        Assert.Equal(
            new[] { "relay-a.test", "relay-b.test" },
            handler.Requests.Select(request => request.Uri.Host));
    }

    [Fact]
    public async Task PublishAsync_RetriesNextEndpointAfterTimeout()
    {
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "relay-a.test")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = TestData.CreateOptions("https://relay-a.test", "https://relay-b.test");
        options.Timeout = TimeSpan.FromMilliseconds(100);
        var publisher = TestData.CreatePublisher(handler, options);

        await publisher.PublishAsync([TestData.CreateEnvelope()], CancellationToken.None);

        Assert.Equal(
            new[] { "relay-a.test", "relay-b.test" },
            handler.Requests.Select(request => request.Uri.Host));
    }

    [Fact]
    public async Task PublishAsync_PropagatesCallerCancellation()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = TestData.CreateOptions("https://relay-a.test");
        options.Timeout = TimeSpan.FromSeconds(30);
        var publisher = TestData.CreatePublisher(handler, options);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publisher.PublishAsync([TestData.CreateEnvelope()], cancellation.Token).AsTask());
    }

    [Fact]
    public async Task PublishAsync_SendsInternalHeaderAndSnakeCaseJsonArray()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var publisher = TestData.CreatePublisher(
            handler,
            TestData.CreateOptions("https://relay-a.test"));

        var envelope = TestData.CreateEnvelope();
        await publisher.PublishAsync([envelope], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://relay-a.test/internal/envelopes", request.Uri.AbsoluteUri);
        Assert.Equal("test-shared-secret", request.Headers["X-GlitchPort-Internal"]);
        Assert.Equal("application/json", request.ContentType);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        var item = Assert.Single(root.EnumerateArray().ToArray());
        Assert.Equal(ExpectedPropertyNames, item.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, item.GetProperty("version").GetInt32());
        Assert.Equal(envelope.EventId, item.GetProperty("event_id").GetGuid());
        Assert.Equal(envelope.ProjectId, item.GetProperty("project_id").GetInt64());
        Assert.Equal(envelope.ReceivedAt, item.GetProperty("received_at").GetDateTimeOffset());
        Assert.Equal(envelope.Endpoint, item.GetProperty("endpoint").GetString());
        Assert.Equal(envelope.Message, item.GetProperty("message").GetString());
        Assert.Equal(envelope.ClientTimestamp, item.GetProperty("client_timestamp").GetDateTimeOffset());
        Assert.Equal(envelope.SdkName, item.GetProperty("sdk_name").GetString());
        Assert.Equal(envelope.SdkVersion, item.GetProperty("sdk_version").GetString());

        var payload = item.GetProperty("payload");
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal("boom", payload.GetProperty("message").GetString());
        Assert.Equal("error", payload.GetProperty("level").GetString());
        Assert.Equal(TestData.ExpectedClientIp, item.GetProperty("client_ip").GetString());
    }

    [Fact]
    public async Task PublishAsync_ThrowsEnvelopePublishExceptionWhenNoEndpointsAreConfigured()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var options = TestData.CreateOptions();
        options.Endpoints = [];
        var publisher = TestData.CreatePublisher(handler, options);

        var exception = await Assert.ThrowsAsync<EnvelopePublishException>(
            () => publisher.PublishAsync([TestData.CreateEnvelope()], CancellationToken.None).AsTask());

        Assert.Empty(exception.AttemptedEndpoints);
        Assert.Empty(handler.Requests);
    }
}
