using System.Text.Json;
using GlitchPort.Collector.Contracts;
using Xunit;

namespace GlitchPort.Transport.Http.Tests;

public sealed class ContractsJsonContextTests
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
    public void Envelope_DefaultsVersionToTwo()
    {
        Assert.Equal(2, new Envelope().Version);
    }

    [Fact]
    public void ContractsJsonContext_SerializesEnvelopeWithSnakeCasePropertyNames()
    {
        var envelope = TestData.CreateEnvelope();

        var json = JsonSerializer.Serialize(envelope, ContractsJsonContext.Default.Envelope);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(ExpectedPropertyNames, root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal(envelope.EventId, root.GetProperty("event_id").GetGuid());
        Assert.Equal(envelope.ProjectId, root.GetProperty("project_id").GetInt64());
        Assert.Equal(envelope.ReceivedAt, root.GetProperty("received_at").GetDateTimeOffset());
        Assert.Equal(envelope.Endpoint, root.GetProperty("endpoint").GetString());
        Assert.Equal(envelope.Message, root.GetProperty("message").GetString());
        Assert.Equal(envelope.ClientTimestamp, root.GetProperty("client_timestamp").GetDateTimeOffset());
        Assert.Equal(envelope.SdkName, root.GetProperty("sdk_name").GetString());
        Assert.Equal(envelope.SdkVersion, root.GetProperty("sdk_version").GetString());

        var payload = root.GetProperty("payload");
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal("boom", payload.GetProperty("message").GetString());
        Assert.Equal("error", payload.GetProperty("level").GetString());

        Assert.Equal(TestData.ExpectedClientIp, root.GetProperty("client_ip").GetString());
    }

    [Fact]
    public void ContractsJsonContext_RoundTripsV1EnvelopeLegacyFields()
    {
        var envelope = TestData.CreateV1Envelope();

        var json = JsonSerializer.Serialize(envelope, ContractsJsonContext.Default.Envelope);
        var roundTripped = Assert.IsType<Envelope>(
            JsonSerializer.Deserialize(json, ContractsJsonContext.Default.Envelope));

        Assert.Equal(1, roundTripped.Version);
        Assert.Equal(envelope.EventId, roundTripped.EventId);
        Assert.Equal(envelope.ProjectId, roundTripped.ProjectId);
        Assert.Equal(envelope.ReceivedAt, roundTripped.ReceivedAt);
        Assert.Equal(envelope.Endpoint, roundTripped.Endpoint);
        Assert.Equal(envelope.Message, roundTripped.Message);
        Assert.Equal(envelope.ClientTimestamp, roundTripped.ClientTimestamp);
        Assert.Equal(envelope.SdkName, roundTripped.SdkName);
        Assert.Equal(envelope.SdkVersion, roundTripped.SdkVersion);
        Assert.Null(roundTripped.Payload);
        Assert.Null(roundTripped.ClientIp);

        // A v1 envelope keeps its wire format: v2-only properties are null.
        Assert.Equal(json, JsonSerializer.Serialize(roundTripped, ContractsJsonContext.Default.Envelope));
    }

    [Fact]
    public void ContractsJsonContext_RoundTripsV2EnvelopePayloadAndClientIp()
    {
        var envelope = TestData.CreateEnvelope();

        var json = JsonSerializer.Serialize(envelope, ContractsJsonContext.Default.Envelope);
        var roundTripped = Assert.IsType<Envelope>(
            JsonSerializer.Deserialize(json, ContractsJsonContext.Default.Envelope));

        Assert.Equal(2, roundTripped.Version);
        Assert.NotNull(roundTripped.Payload);

        var payload = roundTripped.Payload.GetValueOrDefault();
        Assert.Equal("boom", payload.GetProperty("message").GetString());
        Assert.Equal("error", payload.GetProperty("level").GetString());
        Assert.Equal(TestData.ExpectedClientIp, roundTripped.ClientIp);

        // JsonElement values are only equal by document identity, so compare the
        // serialized JSON instead of relying on record equality.
        Assert.Equal(json, JsonSerializer.Serialize(roundTripped, ContractsJsonContext.Default.Envelope));
    }

    [Fact]
    public void ContractsJsonContext_RoundTripsEnvelopeArray()
    {
        var envelopes = new[] { TestData.CreateEnvelope(), TestData.CreateEnvelope() };

        var json = SerializeEnvelopeArray(envelopes);
        var roundTripped = Assert.IsType<Envelope[]>(
            JsonSerializer.Deserialize(json, ContractsJsonContext.Default.EnvelopeArray));

        Assert.Equal(json, SerializeEnvelopeArray(roundTripped));
    }

    [Fact]
    public void ContractsJsonContext_RoundTripsEnvelopeList()
    {
        var envelopes = new List<Envelope> { TestData.CreateEnvelope() };

        var json = SerializeEnvelopeList(envelopes);
        var roundTripped = Assert.IsType<List<Envelope>>(
            JsonSerializer.Deserialize(json, ContractsJsonContext.Default.ListEnvelope));

        Assert.Equal(json, SerializeEnvelopeList(roundTripped));
    }

    private static string SerializeEnvelopeArray(Envelope[] envelopes) =>
        JsonSerializer.Serialize(envelopes, ContractsJsonContext.Default.EnvelopeArray);

    private static string SerializeEnvelopeList(List<Envelope> envelopes) =>
        JsonSerializer.Serialize(envelopes, ContractsJsonContext.Default.ListEnvelope);
}
