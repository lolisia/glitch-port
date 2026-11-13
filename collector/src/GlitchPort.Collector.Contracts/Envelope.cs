namespace GlitchPort.Collector.Contracts;

/// <summary>
/// A single event envelope exchanged between the Relay and the Processor.
/// </summary>
public sealed record Envelope
{
    /// <summary>Envelope schema version.</summary>
    public int Version { get; init; } = 1;

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

    /// <summary>Human-readable event message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Timestamp reported by the client SDK, when available.</summary>
    public DateTimeOffset? ClientTimestamp { get; init; }

    /// <summary>Name of the client SDK, when available.</summary>
    public string? SdkName { get; init; }

    /// <summary>Version of the client SDK, when available.</summary>
    public string? SdkVersion { get; init; }
}
