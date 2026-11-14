using System.Text.Json;

namespace GlitchPort.Relay;

/// <summary>
/// The normalized protocol v1 event that is stored in
/// <see cref="GlitchPort.Collector.Contracts.Envelope.Payload"/>. Optional
/// fields are omitted when they are absent.
/// </summary>
internal sealed class EventV1Payload
{
    /// <summary>Canonical lowercase UUID of the event.</summary>
    public string? EventId { get; init; }

    /// <summary>ISO 8601 timestamp of the event, in UTC.</summary>
    public string? Timestamp { get; init; }

    /// <summary>Normalized event level.</summary>
    public string? Level { get; init; }

    /// <summary>Platform or SDK family.</summary>
    public string? Platform { get; init; }

    /// <summary>Logger or subsystem that produced the event.</summary>
    public string? Logger { get; init; }

    /// <summary>Human-readable event message.</summary>
    public string? Message { get; init; }

    /// <summary>Release identifier.</summary>
    public string? Release { get; init; }

    /// <summary>Deployment environment.</summary>
    public string? Environment { get; init; }

    /// <summary>Server that produced the event.</summary>
    public string? ServerName { get; init; }

    /// <summary>Client SDK that produced the event.</summary>
    public SdkPayload? Sdk { get; init; }

    /// <summary>Exceptions ordered inner to outer.</summary>
    public ExceptionPayload[]? Exception { get; init; }

    /// <summary>String key/value tags.</summary>
    public Dictionary<string, string>? Tags { get; init; }

    /// <summary>Information about the affected user.</summary>
    public UserPayload? User { get; init; }

    /// <summary>Free-form context namespaces.</summary>
    public JsonElement? Contexts { get; init; }

    /// <summary>Breadcrumbs in chronological order.</summary>
    public BreadcrumbPayload[]? Breadcrumbs { get; init; }

    /// <summary>Custom grouping key.</summary>
    public string[]? Fingerprint { get; init; }

    /// <summary>Free-form additional data.</summary>
    public JsonElement? Extra { get; init; }
}

/// <summary>SDK identification stored in a protocol v1 payload.</summary>
internal sealed class SdkPayload
{
    /// <summary>SDK package name.</summary>
    public string? Name { get; init; }

    /// <summary>SDK package version.</summary>
    public string? Version { get; init; }
}

/// <summary>A single exception stored in a protocol v1 payload.</summary>
internal sealed class ExceptionPayload
{
    /// <summary>Exception type; never blank.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Exception message or value.</summary>
    public string? Value { get; init; }

    /// <summary>Assembly, module or namespace that defines the exception.</summary>
    public string? Module { get; init; }

    /// <summary>Stack trace captured for this exception.</summary>
    public StacktracePayload? Stacktrace { get; init; }
}

/// <summary>The stack trace of a single exception.</summary>
internal sealed class StacktracePayload
{
    /// <summary>Frames ordered deepest to shallowest.</summary>
    public StackFramePayload[]? Frames { get; init; }
}

/// <summary>A single stack frame stored in a protocol v1 payload.</summary>
internal sealed class StackFramePayload
{
    /// <summary>Function or method name.</summary>
    public string? Function { get; init; }

    /// <summary>Module, assembly or namespace that contains the frame.</summary>
    public string? Module { get; init; }

    /// <summary>Source file name as reported by the runtime.</summary>
    public string? Filename { get; init; }

    /// <summary>Absolute path of the source file.</summary>
    public string? AbsPath { get; init; }

    /// <summary>Source line at <see cref="Lineno"/>.</summary>
    public string? ContextLine { get; init; }

    /// <summary>1-based line number in <see cref="Filename"/>.</summary>
    public long? Lineno { get; init; }

    /// <summary>1-based column number in <see cref="Filename"/>.</summary>
    public long? Colno { get; init; }

    /// <summary>True when the frame belongs to the game's own code.</summary>
    public bool? InApp { get; init; }
}

/// <summary>A single breadcrumb stored in a protocol v1 payload.</summary>
internal sealed class BreadcrumbPayload
{
    /// <summary>ISO 8601 time at which the breadcrumb was recorded.</summary>
    public string? Timestamp { get; init; }

    /// <summary>Breadcrumb category.</summary>
    public string? Category { get; init; }

    /// <summary>Human-readable breadcrumb message.</summary>
    public string? Message { get; init; }

    /// <summary>Severity of the breadcrumb.</summary>
    public string? Level { get; init; }

    /// <summary>Breadcrumb type.</summary>
    public string? Type { get; init; }

    /// <summary>Free-form JSON data attached to the breadcrumb.</summary>
    public JsonElement? Data { get; init; }
}

/// <summary>Information about the user affected by an event.</summary>
internal sealed class UserPayload
{
    /// <summary>Stable user identifier.</summary>
    public string? Id { get; init; }

    /// <summary>User name.</summary>
    public string? Username { get; init; }

    /// <summary>User e-mail address.</summary>
    public string? Email { get; init; }

    /// <summary>IPv4 or IPv6 address of the user.</summary>
    public string? IpAddress { get; init; }
}

/// <summary>
/// A validated and normalized event, together with the values copied to the
/// legacy envelope fields.
/// </summary>
internal sealed record NormalizedEvent
{
    /// <summary>Id of the event.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Normalized protocol v1 payload.</summary>
    public required EventV1Payload Payload { get; init; }

    /// <summary>Human-readable message used by the envelope compatibility field.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Timestamp reported by the client, when available.</summary>
    public DateTimeOffset? ClientTimestamp { get; init; }

    /// <summary>Name of the client SDK, when available.</summary>
    public string? SdkName { get; init; }

    /// <summary>Version of the client SDK, when available.</summary>
    public string? SdkVersion { get; init; }
}

/// <summary>Result of validating and normalizing a request body.</summary>
internal readonly record struct NormalizeResult(NormalizedEvent? Event, string? Error)
{
    /// <summary>True when the event was accepted.</summary>
    public bool IsSuccess => Event is not null;

    /// <summary>Creates a successful result.</summary>
    public static NormalizeResult Success(NormalizedEvent value) => new(value, null);

    /// <summary>Creates a failed result with a <c>path: reason</c> message.</summary>
    public static NormalizeResult Failure(string error) => new(null, error);
}
