using GlitchPort.Collector.Contracts;

namespace GlitchPort.Transport.Http.Tests;

internal static class TestData
{
    public static Envelope CreateEnvelope(Guid? eventId = null) => new()
    {
        EventId = eventId ?? Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
        ProjectId = 42,
        ReceivedAt = new DateTimeOffset(2026, 11, 14, 8, 30, 0, TimeSpan.Zero),
        Endpoint = "POST /api/1/messages",
        Message = "Test crash message",
        ClientTimestamp = new DateTimeOffset(2026, 11, 14, 8, 29, 59, TimeSpan.Zero),
        SdkName = "glitch-port-unreal",
        SdkVersion = "1.2.3",
    };

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
