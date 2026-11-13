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
    ];

    [Fact]
    public void Envelope_DefaultsVersionToOne()
    {
        Assert.Equal(1, new Envelope().Version);
    }

    [Fact]
    public void ContractsJsonContext_SerializesEnvelopeWithSnakeCasePropertyNames()
    {
        var envelope = TestData.CreateEnvelope();

        var json = JsonSerializer.Serialize(envelope, ContractsJsonContext.Default.Envelope);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(ExpectedPropertyNames, root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(envelope.EventId, root.GetProperty("event_id").GetGuid());
        Assert.Equal(envelope.ProjectId, root.GetProperty("project_id").GetInt64());
        Assert.Equal(envelope.ReceivedAt, root.GetProperty("received_at").GetDateTimeOffset());
        Assert.Equal(envelope.Endpoint, root.GetProperty("endpoint").GetString());
        Assert.Equal(envelope.Message, root.GetProperty("message").GetString());
        Assert.Equal(envelope.ClientTimestamp, root.GetProperty("client_timestamp").GetDateTimeOffset());
        Assert.Equal(envelope.SdkName, root.GetProperty("sdk_name").GetString());
        Assert.Equal(envelope.SdkVersion, root.GetProperty("sdk_version").GetString());
    }

    [Fact]
    public void ContractsJsonContext_RoundTripsEnvelopeArray()
    {
        var envelopes = new[] { TestData.CreateEnvelope(), TestData.CreateEnvelope() };

        var json = JsonSerializer.SerializeToUtf8Bytes(envelopes, ContractsJsonContext.Default.EnvelopeArray);
        var roundTripped = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.EnvelopeArray);

        Assert.Equal(envelopes, roundTripped);
    }

    [Fact]
    public void ContractsJsonContext_RoundTripsEnvelopeList()
    {
        var envelopes = new List<Envelope> { TestData.CreateEnvelope() };

        var json = JsonSerializer.Serialize(envelopes, ContractsJsonContext.Default.ListEnvelope);
        var roundTripped = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.ListEnvelope);

        Assert.Equal(envelopes, roundTripped);
    }
}
