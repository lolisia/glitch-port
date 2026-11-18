namespace GlitchPort.Relay;

/// <summary>Protocol constants shared by the Relay endpoints.</summary>
internal static class RelayConstants
{
    /// <summary>Header that carries the project's public ingest key.</summary>
    public const string PublicKeyHeader = "X-GlitchPort-Key";

    /// <summary>Header that declares an optional request body compression.</summary>
    public const string ContentEncodingHeader = "Content-Encoding";

    /// <summary>Compression supported by the ingest endpoints.</summary>
    public const string GzipEncoding = "gzip";

    /// <summary>Maximum accepted size of a decompressed request body, in bytes (1 MB).</summary>
    public const int MaxRequestBodyBytes = 1024 * 1024;

    /// <summary>Seconds clients should wait before retrying a failed publish.</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>Maximum accepted length of the event message, in characters.</summary>
    public const int MaxMessageLength = 8192;

    /// <summary>Maximum accepted length of an exception value, in characters.</summary>
    public const int MaxExceptionValueLength = 8192;

    /// <summary>Maximum accepted length of an exception type, in characters.</summary>
    public const int MaxExceptionTypeLength = 200;

    /// <summary>Maximum accepted length of an exception module, in characters.</summary>
    public const int MaxExceptionModuleLength = 200;

    /// <summary>
    /// Maximum accepted length of the platform field, in characters. Protocol
    /// v1 platforms are lowercased and must match <c>[a-z0-9._-]</c>.
    /// </summary>
    public const int MaxPlatformLength = 32;

    /// <summary>Maximum accepted length of the logger field, in characters.</summary>
    public const int MaxLoggerLength = 128;

    /// <summary>Maximum accepted length of the release field, in characters.</summary>
    public const int MaxReleaseLength = 200;

    /// <summary>Maximum accepted length of the environment field, in characters.</summary>
    public const int MaxEnvironmentLength = 64;

    /// <summary>Maximum accepted length of the server name field, in characters.</summary>
    public const int MaxServerNameLength = 200;

    /// <summary>Maximum accepted length of a frame string, in characters.</summary>
    public const int MaxFrameStringLength = 1024;

    /// <summary>Maximum accepted length of a tag key, in characters.</summary>
    public const int MaxTagKeyLength = 32;

    /// <summary>Maximum accepted length of a tag value, in characters.</summary>
    public const int MaxTagValueLength = 200;

    /// <summary>Maximum accepted length of a fingerprint entry, in characters.</summary>
    public const int MaxFingerprintEntryLength = 200;

    /// <summary>Maximum number of exceptions kept from an event.</summary>
    public const int MaxExceptions = 10;

    /// <summary>Maximum number of stack frames kept per exception (the last frames).</summary>
    public const int MaxFrames = 250;

    /// <summary>Maximum number of breadcrumbs kept from an event (the first entries).</summary>
    public const int MaxBreadcrumbs = 100;

    /// <summary>Maximum number of tags kept from an event (the first entries).</summary>
    public const int MaxTags = 50;

    /// <summary>Maximum number of fingerprint entries kept from an event.</summary>
    public const int MaxFingerprintEntries = 10;

    /// <summary>Maximum serialized size of <c>contexts</c> or <c>extra</c>, in bytes (64 KB).</summary>
    public const int MaxFreeFormBytes = 64 * 1024;

    /// <summary>Platform used by the legacy messages endpoint when none is supplied.</summary>
    public const string DefaultPlatform = "other";

    /// <summary>Level used for message events that do not declare one.</summary>
    public const string DefaultMessageLevel = "info";

    /// <summary>Level used for exception events that do not declare one.</summary>
    public const string DefaultExceptionLevel = "error";
}
