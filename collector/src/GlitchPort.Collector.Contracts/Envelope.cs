using System.Text.Json;

namespace GlitchPort.Collector.Contracts;

/// <summary>
/// A single event envelope exchanged between the Relay and the Processor.
/// </summary>
public sealed record Envelope
{
    /// <summary>Envelope schema version. Defaults to the current version, 2.</summary>
    public int Version { get; init; } = 2;

    /// <summary>Stable, unique identifier of the event.</summary>
    public Guid EventId { get; init; }

    /// <summary>Identifier of the project that owns the event.</summary>
    public long ProjectId { get; init; }

    /// <summary>Time at which the Relay accepted the event, in UTC.</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>
    /// Logical endpoint that received the event, for example
    /// <c>POST /api/1/messages</c>.
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable event message. Kept for v1 compatibility; v2 consumers
    /// treat <see cref="Payload"/> as authoritative.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp reported by the client SDK, when available. Kept for v1
    /// compatibility.
    /// </summary>
    public DateTimeOffset? ClientTimestamp { get; init; }

    /// <summary>
    /// Name of the client SDK, when available. Kept for v1 compatibility.
    /// </summary>
    public string? SdkName { get; init; }

    /// <summary>
    /// Version of the client SDK, when available. Kept for v1 compatibility.
    /// </summary>
    public string? SdkVersion { get; init; }

    /// <summary>
    /// Normalized v1 event JSON (for example <c>{ "message": ..., "level": ... }</c>).
    /// Authoritative in protocol v2; v1 envelopes leave this <see langword="null"/>.
    /// </summary>
    public JsonElement? Payload { get; init; }

    /// <summary>IP address of the client that reported the event, when available.</summary>
    public string? ClientIp { get; init; }
}
