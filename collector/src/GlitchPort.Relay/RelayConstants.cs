namespace GlitchPort.Relay;

/// <summary>Protocol constants shared by the Relay endpoints.</summary>
internal static class RelayConstants
{
    /// <summary>Header that carries the project's public ingest key.</summary>
    public const string PublicKeyHeader = "X-GlitchPort-Key";

    /// <summary>Maximum accepted length of the event message, in characters.</summary>
    public const int MaxMessageLength = 8192;
}
