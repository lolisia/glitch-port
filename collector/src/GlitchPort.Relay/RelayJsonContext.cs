using System.Text.Json.Serialization;

namespace GlitchPort.Relay;

/// <summary>
/// JSON contract for the public Relay API. All payloads are snake_case on the
/// wire.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(IngestMessageRequest))]
[JsonSerializable(typeof(IngestMessageResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class RelayJsonContext : JsonSerializerContext
{
}

/// <summary>Request body accepted by <c>POST /api/{projectId}/messages</c>.</summary>
internal sealed class IngestMessageRequest
{
    /// <summary>Optional client-generated event id. Generated when absent.</summary>
    public Guid? EventId { get; set; }

    /// <summary>Optional client timestamp, in ISO-8601 format.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>Required event message.</summary>
    public string? Message { get; set; }

    /// <summary>Optional description of the client SDK that sent the event.</summary>
    public SdkInfo? Sdk { get; set; }
}

/// <summary>SDK identification sent with an event.</summary>
internal sealed class SdkInfo
{
    /// <summary>SDK name, for example <c>glitchport-unreal</c>.</summary>
    public string? Name { get; set; }

    /// <summary>SDK version, for example <c>1.2.3</c>.</summary>
    public string? Version { get; set; }
}

/// <summary>Response body for an accepted event.</summary>
internal sealed class IngestMessageResponse
{
    /// <summary>Id of the accepted event.</summary>
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
