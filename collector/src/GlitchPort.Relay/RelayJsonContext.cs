using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlitchPort.Relay;

/// <summary>
/// JSON contract for the public Relay API. All payloads are snake_case on the
/// wire and every type that is written to a response or envelope payload is
/// produced by the source generator, so the Relay stays reflection-free and
/// AOT-compatible.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(EventV1Payload))]
[JsonSerializable(typeof(IngestResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class RelayJsonContext : JsonSerializerContext
{
}

/// <summary>Response body for an accepted event.</summary>
internal sealed class IngestResponse
{
    /// <summary>Id of the accepted event, as a canonical lowercase UUID.</summary>
    public Guid Id { get; init; }
}

/// <summary>Response body for a failed request.</summary>
internal sealed class ErrorResponse
{
    /// <summary>Human-readable description of the failure.</summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary>Response body for the health endpoint.</summary>
internal sealed class HealthResponse
{
    /// <summary>Health status of the Relay.</summary>
    public string Status { get; init; } = "ok";
}
