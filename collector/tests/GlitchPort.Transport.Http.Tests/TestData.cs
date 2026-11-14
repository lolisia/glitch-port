using System.Text.Json;
using GlitchPort.Collector.Contracts;

namespace GlitchPort.Transport.Http.Tests;

internal static class TestData
{
    public const string ExpectedClientIp = "203.0.113.7";

    private static readonly Guid DefaultEventId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    public static Envelope CreateEnvelope(Guid? eventId = null) => CreateV1Envelope(eventId) with
    {
        Version = 2,
        Payload = CreatePayload(),
        ClientIp = ExpectedClientIp,
    };

    public static Envelope CreateV1Envelope(Guid? eventId = null) => new()
    {
        Version = 1,
        EventId = eventId ?? DefaultEventId,
        ProjectId = 42,
        ReceivedAt = new DateTimeOffset(2026, 11, 14, 8, 30, 0, TimeSpan.Zero),
        Endpoint = "POST /api/1/messages",
        Message = "Test crash message",
        ClientTimestamp = new DateTimeOffset(2026, 11, 14, 8, 29, 59, TimeSpan.Zero),
        SdkName = "glitch-port-unreal",
        SdkVersion = "1.2.3",
    };

    public static JsonElement CreatePayload() =>
        JsonSerializer.SerializeToElement(new { message = "boom", level = "error" });

    public static HttpTransportOptions CreateOptions(params string[] endpoints) => new()
    {
        Endpoints = endpoints,
        SharedSecret = "test-shared-secret",
        Timeout = TimeSpan.FromSeconds(2),
    };

    public static HttpEnvelopePublisher CreatePublisher(
        FakeHttpMessageHandler handler,
        HttpTransportOptions options) =>
        new(options, new HttpClient(handler));
}
