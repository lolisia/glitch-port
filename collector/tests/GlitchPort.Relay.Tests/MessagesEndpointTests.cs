using System.Globalization;
using System.Net;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class MessagesEndpointTests
{
    [Fact]
    public async Task PostMessage_ConvertsLegacyBodyToV1Event()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        var eventId = Guid.NewGuid();
        var timestamp = DateTimeOffset.Parse(
            "2026-11-14T12:00:00.000Z",
            CultureInfo.InvariantCulture);

        var json = JsonSerializer.Serialize(new
        {
            event_id = eventId,
            timestamp = "2026-11-14T12:00:00.000Z",
            message = "  The matchmaking backend connection was lost  ",
            sdk = new { name = "glitchport.dotnet", version = "0.1.0" },
        });

        using var response = await RelayTestClient.PostMessageAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await RelayTestClient.ReadJsonAsync(response);
        Assert.Equal(eventId, body.GetProperty("id").GetGuid());

        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(2, envelope.Version);
        Assert.Equal("POST /api/1/messages", envelope.Endpoint);
        Assert.Equal("The matchmaking backend connection was lost", envelope.Message);
        Assert.Equal(timestamp, envelope.ClientTimestamp);
        Assert.Equal("glitchport.dotnet", envelope.SdkName);
        Assert.Equal("0.1.0", envelope.SdkVersion);

        var payload = RelayTestClient.Payload(envelope);
        Assert.Equal(eventId.ToString("D"), payload.GetProperty("event_id").GetString());
        Assert.Equal("2026-11-14T12:00:00.000Z", payload.GetProperty("timestamp").GetString());
        Assert.Equal("info", payload.GetProperty("level").GetString());
        Assert.Equal("other", payload.GetProperty("platform").GetString());
        Assert.Equal(
            "The matchmaking backend connection was lost",
            payload.GetProperty("message").GetString());
        Assert.False(payload.TryGetProperty("exception", out _));
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("info")]
    [InlineData("warning")]
    [InlineData("error")]
    [InlineData("fatal")]
    public async Task PostMessage_UsesSubmittedLevel(string level)
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        var json = JsonSerializer.Serialize(new { level, message = "boom" });
        using var response = await RelayTestClient.PostMessageAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(level, RelayTestClient.PayloadString(envelope, "level"));
    }

    [Theory]
    [InlineData("""{"level":"verbose","message":"boom"}""", "level:")]
    [InlineData("""{"level":"","message":"boom"}""", "level:")]
    [InlineData("""{"level":7,"message":"boom"}""", "level:")]
    [InlineData("""{"timestamp":"yesterday","message":"boom"}""", "timestamp:")]
    [InlineData("""{"timestamp":7,"message":"boom"}""", "timestamp:")]
    [InlineData("""{"event_id":"not-a-guid","message":"boom"}""", "event_id:")]
    [InlineData("""{"event_id":7,"message":"boom"}""", "event_id:")]
    [InlineData("""{"message":7}""", "message:")]
    [InlineData("""{}""", "message:")]
    public async Task PostMessage_WithFormatError_ReturnsBadRequestWithFieldPath(
        string json,
        string expectedPrefix)
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostMessageAsync(client, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await RelayTestClient.ReadErrorAsync(response);
        Assert.StartsWith(expectedPrefix, error, StringComparison.Ordinal);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WithGzipBody_IsDecoded()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostMessageAsync(
            client,
            """{"message":"compressed boom"}""",
            gzip: true);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal("compressed boom", RelayTestClient.PayloadString(envelope, "message"));
    }

    [Fact]
    public async Task PostMessage_WithBodyLargerThanOneMegabyte_ReturnsPayloadTooLarge()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        var json = JsonSerializer.Serialize(new { message = new string('a', 1_100_000) });
        using var response = await RelayTestClient.PostMessageAsync(client, json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostMessage_WhenPublisherFails_ReturnsServiceUnavailableWithRetryAfter()
    {
        var publisher = new ThrowingEnvelopePublisher(new InvalidOperationException("processor down"));
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostMessageAsync(
            client,
            """{"message":"boom"}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
    }
}
