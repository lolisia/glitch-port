using System.Globalization;
using System.Net;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class EventsEndpointTests
{
    [Fact]
    public async Task PostEvent_WithMessage_ReturnsAcceptedAndBuildsV2Envelope()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        const string Json = """
            {
              "event_id": "1A2B3C4D5E6F708192A3B4C5D6E7F809",
              "timestamp": "2026-11-14T09:30:00.000Z",
              "level": "warning",
              "platform": " unity ",
              "message": "  boom  ",
              "sdk": { "name": "glitchport.unity", "version": "0.1.0" }
            }
            """;

        var receivedBefore = DateTimeOffset.UtcNow;
        using var response = await RelayTestClient.PostEventAsync(client, Json);
        var receivedAfter = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var expectedId = Guid.Parse("1a2b3c4d-5e6f-7081-92a3-b4c5d6e7f809");
        var body = await RelayTestClient.ReadJsonAsync(response);
        Assert.Equal(expectedId, body.GetProperty("id").GetGuid());
        Assert.Equal(expectedId.ToString("D"), body.GetProperty("id").GetString());

        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(2, envelope.Version);
        Assert.Equal(RelayTestClient.ProjectId, envelope.ProjectId);
        Assert.Equal(expectedId, envelope.EventId);
        Assert.Equal("POST /api/1/events", envelope.Endpoint);
        Assert.Equal("boom", envelope.Message);
        Assert.Equal("glitchport.unity", envelope.SdkName);
        Assert.Equal("0.1.0", envelope.SdkVersion);
        Assert.Equal(RelayTestClient.DefaultRemoteIp, envelope.ClientIp);
        Assert.Equal(
            DateTimeOffset.Parse("2026-11-14T09:30:00.000Z", CultureInfo.InvariantCulture),
            envelope.ClientTimestamp);
        Assert.Equal(TimeSpan.Zero, envelope.ReceivedAt.Offset);
        Assert.InRange(envelope.ReceivedAt, receivedBefore, receivedAfter);

        var payload = RelayTestClient.Payload(envelope);
        Assert.Equal(expectedId.ToString("D"), payload.GetProperty("event_id").GetString());
        Assert.Equal("2026-11-14T09:30:00.000Z", payload.GetProperty("timestamp").GetString());
        Assert.Equal("warning", payload.GetProperty("level").GetString());
        Assert.Equal("unity", payload.GetProperty("platform").GetString());
        Assert.Equal("boom", payload.GetProperty("message").GetString());
        Assert.Equal(
            "glitchport.unity",
            payload.GetProperty("sdk").GetProperty("name").GetString());
        Assert.Equal(
            "0.1.0",
            payload.GetProperty("sdk").GetProperty("version").GetString());
    }

    [Fact]
    public async Task PostEvent_WithoutEventId_GeneratesOneForResponseAndPayload()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom","platform":"unity"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await RelayTestClient.ReadJsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);

        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(id, envelope.EventId);
        Assert.Equal(
            id.ToString("D"),
            RelayTestClient.Payload(envelope).GetProperty("event_id").GetString());
    }

    [Fact]
    public async Task PostEvent_WithoutTimestamp_UsesReceiveTimeInPayload()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom","platform":"unity"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Null(envelope.ClientTimestamp);

        var timestamp = RelayTestClient.Payload(envelope)
            .GetProperty("timestamp")
            .GetString();
        var parsed = DateTimeOffset.Parse(timestamp!, CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(
            envelope.ReceivedAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            parsed.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("""{"message":"boom","platform":"unity"}""", "info")]
    [InlineData("""{"exception":[{"type":"System.Exception"}],"platform":"unity"}""", "error")]
    [InlineData("""{"message":"boom","exception":[],"platform":"unity"}""", "info")]
    [InlineData("""{"message":"boom","level":"fatal","platform":"unity"}""", "fatal")]
    public async Task PostEvent_ResolvesLevelDefault(string json, string expectedLevel)
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(expectedLevel, RelayTestClient.PayloadString(envelope, "level"));
    }

    [Fact]
    public async Task PostEvent_WithoutPlatform_ReturnsBadRequest()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("platform is required", await RelayTestClient.ReadErrorAsync(response));
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithUppercasePlatform_StoresItLowercased()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom","platform":"Unity"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal("unity", RelayTestClient.PayloadString(envelope, "platform"));
    }

    [Fact]
    public async Task PostEvent_PreservesExceptionInnerToOuterAndFramesDeepestToShallowest()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        const string Json = """
            {
              "message": "outer",
              "platform": "unity",
              "exception": [
                {
                  "type": "Inner",
                  "value": "inner",
                  "stacktrace": { "frames": [
                    { "function": "deepest" },
                    { "function": "middle" },
                    { "function": "crash" }
                  ] }
                },
                { "type": "Middle", "value": "middle" },
                { "type": "Outer", "value": "outer" }
              ]
            }
            """;

        using var response = await RelayTestClient.PostEventAsync(client, Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));

        var exceptions = payload.GetProperty("exception");
        Assert.Equal(3, exceptions.GetArrayLength());
        Assert.Equal("Inner", exceptions[0].GetProperty("type").GetString());
        Assert.Equal("Middle", exceptions[1].GetProperty("type").GetString());
        Assert.Equal("Outer", exceptions[2].GetProperty("type").GetString());

        var frames = exceptions[0]
            .GetProperty("stacktrace")
            .GetProperty("frames");
        Assert.Equal(3, frames.GetArrayLength());
        Assert.Equal("deepest", frames[0].GetProperty("function").GetString());
        Assert.Equal("middle", frames[1].GetProperty("function").GetString());
        Assert.Equal("crash", frames[2].GetProperty("function").GetString());
    }

    [Theory]
    [InlineData("""{"level":"verbose","message":"boom","platform":"unity"}""", "level:")]
    [InlineData("""{"level":"","message":"boom","platform":"unity"}""", "level:")]
    [InlineData("""{"level":3,"message":"boom","platform":"unity"}""", "level:")]
    [InlineData("""{"event_id":"not-a-guid","message":"boom","platform":"unity"}""", "event_id:")]
    [InlineData("""{"event_id":42,"message":"boom","platform":"unity"}""", "event_id:")]
    [InlineData("""{"event_id":"","message":"boom","platform":"unity"}""", "event_id:")]
    [InlineData("""{"timestamp":"yesterday","message":"boom","platform":"unity"}""", "timestamp:")]
    [InlineData("""{"timestamp":"2026-11-14T09:30:00","message":"boom","platform":"unity"}""", "timestamp:")]
    [InlineData("""{"timestamp":123,"message":"boom","platform":"unity"}""", "timestamp:")]
    [InlineData("""{"message":42,"platform":"unity"}""", "message:")]
    [InlineData("""{"exception":[{"value":"no type"}],"platform":"unity"}""", "exception[0].type:")]
    [InlineData("""{"exception":[{"type":"   "}],"platform":"unity"}""", "exception[0].type:")]
    [InlineData("""{"exception":[{"type":42}],"platform":"unity"}""", "exception[0].type:")]
    [InlineData("""{"exception":"boom","platform":"unity"}""", "exception:")]
    [InlineData("""{"tags":{"region":7},"message":"boom","platform":"unity"}""", "tags.")]
    [InlineData("""{"tags":[],"message":"boom","platform":"unity"}""", "tags:")]
    [InlineData("""{"fingerprint":[7],"message":"boom","platform":"unity"}""", "fingerprint[0]:")]
    [InlineData("""{"fingerprint":"boom","message":"boom","platform":"unity"}""", "fingerprint:")]
    [InlineData("""{"user":[],"message":"boom","platform":"unity"}""", "user:")]
    [InlineData("""{"sdk":"glitchport","message":"boom","platform":"unity"}""", "sdk:")]
    [InlineData("""{"contexts":[],"message":"boom","platform":"unity"}""", "contexts:")]
    [InlineData("""{"extra":"boom","message":"boom","platform":"unity"}""", "extra:")]
    [InlineData("""{"breadcrumbs":{},"message":"boom","platform":"unity"}""", "breadcrumbs:")]
    [InlineData(
        """{"breadcrumbs":[{"level":"verbose"}],"message":"boom","platform":"unity"}""",
        "breadcrumbs[0].level:")]
    [InlineData(
        """{"breadcrumbs":[{"timestamp":"soon"}],"message":"boom","platform":"unity"}""",
        "breadcrumbs[0].timestamp:")]
    [InlineData(
        """{"exception":[{"type":"T","stacktrace":{"frames":[{"lineno":"one"}]}}],"platform":"unity"}""",
        "exception[0].stacktrace.frames[0].lineno:")]
    [InlineData(
        """{"exception":[{"type":"T","stacktrace":{"frames":[{"in_app":"yes"}]}}],"platform":"unity"}""",
        "exception[0].stacktrace.frames[0].in_app:")]
    [InlineData(
        """{"exception":[{"type":"T","stacktrace":"boom"}],"platform":"unity"}""",
        "exception[0].stacktrace:")]
    [InlineData(
        """{"exception":[{"type":"T","stacktrace":{"frames":{}}}],"platform":"unity"}""",
        "exception[0].stacktrace.frames:")]
    [InlineData("""{"platform":"unity"}""", "message:")]
    [InlineData("""{"message":null,"platform":"unity"}""", "message:")]
    [InlineData("""{"exception":[],"platform":"unity"}""", "message:")]
    [InlineData("""[]""", "body:")]
    [InlineData("""{"message":}""", "body:")]
    public async Task PostEvent_WithFormatError_ReturnsBadRequestWithFieldPath(
        string json,
        string expectedPrefix)
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await RelayTestClient.ReadErrorAsync(response);
        Assert.StartsWith(expectedPrefix, error, StringComparison.Ordinal);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithUnknownTopLevelFields_IgnoresThem()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom","platform":"unity","unknown_field":{"nested":true},"another":7}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = RelayTestClient.Payload(Assert.Single(publisher.Envelopes));
        Assert.False(payload.TryGetProperty("unknown_field", out _));
        Assert.False(payload.TryGetProperty("another", out _));
    }

    [Fact]
    public async Task PostEvent_WithoutKey_ReturnsUnauthorized()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom"}""",
            key: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithWrongKey_ReturnsUnauthorized()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom"}""",
            key: "wrong-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_ForUnknownProject_ReturnsUnauthorized()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostJsonAsync(
            client,
            RelayTestClient.EventsPath(projectId: 2),
            """{"message":"boom"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithGzipBody_IsDecoded()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"compressed boom","platform":"unity"}""",
            gzip: true);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal("compressed boom", RelayTestClient.PayloadString(envelope, "message"));
    }

    [Fact]
    public async Task PostEvent_WithUnsupportedContentEncoding_ReturnsBadRequest()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom"}""",
            contentEncoding: "br");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await RelayTestClient.ReadErrorAsync(response);
        Assert.StartsWith("Content-Encoding:", error, StringComparison.Ordinal);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithInvalidGzipBody_ReturnsBadRequest()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom"}""",
            contentEncoding: "gzip");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await RelayTestClient.ReadErrorAsync(response);
        Assert.StartsWith("Content-Encoding:", error, StringComparison.Ordinal);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithTruncatedGzipBody_ReturnsBadRequest()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        var compressed = RelayTestClient.CompressJson("""{"message":"boom"}""");
        var truncated = compressed[..(compressed.Length / 2)];

        using var response = await RelayTestClient.PostBytesAsync(
            client,
            RelayTestClient.EventsPath(),
            truncated,
            contentEncoding: "gzip");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithBodyLargerThanOneMegabyte_ReturnsPayloadTooLarge()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        var json = JsonSerializer.Serialize(new { message = new string('a', 1_100_000) });
        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await RelayTestClient.ReadErrorAsync(response)));
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithGzipBodyDecompressingOverOneMegabyte_ReturnsPayloadTooLarge()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        var json = JsonSerializer.Serialize(new { message = new string('a', 1_100_000) });
        using var response = await RelayTestClient.PostEventAsync(client, json, gzip: true);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(publisher.Envelopes);
    }

    [Fact]
    public async Task PostEvent_WithBodyOfExactlyOneMegabyte_IsAccepted()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        // The JSON envelope around the message and the platform is 33 bytes.
        var overhead = JsonSerializer
            .Serialize(new { message = string.Empty, platform = "unity" })
            .Length;
        Assert.Equal(33, overhead);
        var json = JsonSerializer.Serialize(
            new { message = new string('a', 1_048_576 - overhead), platform = "unity" });
        Assert.Equal(1_048_576, json.Length);

        using var response = await RelayTestClient.PostEventAsync(client, json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(8192, RelayTestClient.PayloadString(envelope, "message")!.Length);
    }

    [Fact]
    public async Task PostEvent_WhenPublisherFails_ReturnsServiceUnavailableWithRetryAfter()
    {
        var publisher = new ThrowingEnvelopePublisher(new InvalidOperationException("processor down"));
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom","platform":"unity"}""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
        Assert.False(string.IsNullOrWhiteSpace(await RelayTestClient.ReadErrorAsync(response)));
    }

    [Fact]
    public async Task PostEvent_ReplacesAutoIpAddressWithRemoteIp()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom","platform":"unity","user":{"id":"player-1","ip_address":"{{auto}}"}}""",
            headers: new Dictionary<string, string>
            {
                [RelayTestClient.RemoteIpHeader] = "198.51.100.23",
            });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal("198.51.100.23", envelope.ClientIp);

        var user = RelayTestClient.Payload(envelope).GetProperty("user");
        Assert.Equal("player-1", user.GetProperty("id").GetString());
        Assert.Equal("198.51.100.23", user.GetProperty("ip_address").GetString());
    }

    [Fact]
    public async Task PostEvent_KeepsExplicitUserIpAddress()
    {
        var publisher = new RecordingEnvelopePublisher();
        using var factory = new RelayApplicationFactory(publisher);
        using var client = await factory.CreateLoadedClientAsync();

        using var response = await RelayTestClient.PostEventAsync(
            client,
            """{"message":"boom","platform":"unity","user":{"ip_address":"198.51.100.7"}}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = Assert.Single(publisher.Envelopes);
        Assert.Equal(
            "198.51.100.7",
            RelayTestClient.Payload(envelope)
                .GetProperty("user")
                .GetProperty("ip_address")
                .GetString());
    }
}
