using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GlitchPort.Collector.Contracts;
using GlitchPort.Transport.Http;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class IngestEndpointTests
{
    private const string ValidJson = """{"message":"boom"}""";

    [Fact]
    public async Task PostMessage_WithoutKey_ReturnsUnauthorized()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var request = CreatePostRequest(ValidJson, key: null);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WithWrongKey_ReturnsUnauthorized()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var request = CreatePostRequest(ValidJson, key: "wrong-key");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_ForUnknownProject_ReturnsUnauthorized()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var request = CreatePostRequest(ValidJson, projectId: 2);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"message":null}""")]
    [InlineData("""{"message":""}""")]
    [InlineData("""{"message":"   "}""")]
    public async Task PostMessage_WithBlankMessage_ReturnsBadRequest(string json)
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var request = CreatePostRequest(json);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WithMessageLongerThanMaximum_ReturnsBadRequest()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var json = JsonSerializer.Serialize(new { message = new string('a', 8193) });
        using var request = CreatePostRequest(json);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WithMaximumLengthMessage_ReturnsAccepted()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var message = new string('a', 8192);
        var json = JsonSerializer.Serialize(new { message });
        using var request = CreatePostRequest(json);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(message, envelope.Message);
    }

    [Theory]
    [InlineData("""{"event_id":"not-a-guid","message":"boom"}""")]
    [InlineData("""{"event_id":42,"message":"boom"}""")]
    public async Task PostMessage_WithMalformedEventId_ReturnsBadRequest(string json)
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var request = CreatePostRequest(json);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WithMalformedJson_ReturnsBadRequest()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var request = CreatePostRequest("{not-json");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WithEventId_ReturnsAcceptedAndSameId()
    {
        var eventId = Guid.NewGuid();
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var json = JsonSerializer.Serialize(new { event_id = eventId, message = "boom" });
        using var request = CreatePostRequest(json);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.Equal(eventId, document.RootElement.GetProperty("id").GetGuid());

        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(eventId, envelope.EventId);
    }

    [Fact]
    public async Task PostMessage_WithoutEventId_GeneratesParseableGuid()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var request = CreatePostRequest(ValidJson);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = await ReadJsonAsync(response);

        var id = document.RootElement.GetProperty("id").GetString();
        Assert.True(Guid.TryParse(id, out var parsedId));
        Assert.NotEqual(Guid.Empty, parsedId);

        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(parsedId, envelope.EventId);
    }

    [Fact]
    public async Task PostMessage_BuildsEnvelopeFromRequest()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        const string Json = """
            {
              "message": "  boom  ",
              "timestamp": "2026-01-02T03:04:05+00:00",
              "sdk": { "name": "glitchport-unreal", "version": "1.2.3" }
            }
            """;

        var receivedBefore = DateTimeOffset.UtcNow;
        using var request = CreatePostRequest(Json);
        using var response = await client.SendAsync(request);
        var receivedAfter = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(1, envelope.Version);
        Assert.Equal(1, envelope.ProjectId);
        Assert.Equal("POST /api/1/messages", envelope.Endpoint);
        Assert.Equal("boom", envelope.Message);
        Assert.Equal(TimeSpan.Zero, envelope.ReceivedAt.Offset);
        Assert.InRange(envelope.ReceivedAt, receivedBefore, receivedAfter);
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-02T03:04:05+00:00", CultureInfo.InvariantCulture),
            envelope.ClientTimestamp);
        Assert.Equal("glitchport-unreal", envelope.SdkName);
        Assert.Equal("1.2.3", envelope.SdkVersion);
    }

    [Fact]
    public async Task PostMessage_WithSdkEventIncludingLevelField_ReturnsAccepted()
    {
        var eventId = Guid.NewGuid();
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        var json = JsonSerializer.Serialize(new
        {
            event_id = eventId,
            timestamp = "2026-11-14T15:02:14.010Z",
            level = "warning",
            message = "boom",
            sdk = new { name = "glitchport.dotnet", version = "0.1.0" },
        });

        using var request = CreatePostRequest(json);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal("boom", envelope.Message);
        Assert.Equal(eventId, envelope.EventId);
    }

    [Fact]
    public async Task PostMessage_WhenPublisherThrowsEnvelopePublishException_ReturnsServiceUnavailable()
    {
        var publisher = new ThrowingEnvelopePublisher(
            new EnvelopePublishException("processor unavailable", ["http://localhost:5090"]));
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var request = CreatePostRequest(ValidJson);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task PostMessage_WhenPublisherThrowsUnexpectedException_ReturnsServiceUnavailable()
    {
        var publisher = new ThrowingEnvelopePublisher(new InvalidOperationException("boom"));
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var request = CreatePostRequest(ValidJson);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetHealthz_ReturnsOkWithoutPublishing()
    {
        var publisher = new ThrowingEnvelopePublisher(new InvalidOperationException("must not publish"));
        using var factory = new RelayApplicationFactory(publisher);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage CreatePostRequest(
        string json,
        string? key = "test-key",
        long projectId = 1)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{projectId}/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("X-GlitchPort-Key", key);
        }

        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }
}
