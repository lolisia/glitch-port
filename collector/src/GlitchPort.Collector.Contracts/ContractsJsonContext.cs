using System.Text.Json.Serialization;

namespace GlitchPort.Collector.Contracts;

/// <summary>
/// Serialization context shared by the Relay and the Processor. Envelope JSON
/// on the wire is snake_case; both sides must use this context so the wire
/// format cannot drift.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(Envelope[]))]
[JsonSerializable(typeof(List<Envelope>))]
public sealed partial class ContractsJsonContext : JsonSerializerContext
{
}
