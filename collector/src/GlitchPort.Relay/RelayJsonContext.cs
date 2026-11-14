using System.Text.Json;
using System.Text.Json.Serialization;
using GlitchPort.Relay.Config;

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
[JsonSerializable(typeof(ProjectConfigResponse))]
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

    /// <summary>State of the Processor project configuration.</summary>
    public HealthConfigResponse Config { get; init; } = new();
}

/// <summary>Project-configuration section of the health response.</summary>
internal sealed class HealthConfigResponse
{
    /// <summary>Either <c>static</c> or <c>processor</c>.</summary>
    public string Source { get; init; } = "static";

    /// <summary>
    /// ISO 8601 UTC timestamp of the last successful refresh, or
    /// <see langword="null"/> when no refresh has succeeded yet.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LastRefresh { get; init; }

    /// <summary>Union of the static and cached project ids.</summary>
    public int ProjectCount { get; init; }
}
