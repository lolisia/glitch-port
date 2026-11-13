namespace GlitchPort.Processor;

/// <summary>
/// Configuration bound from the <c>Processor</c> configuration section.
/// </summary>
public sealed class ProcessorOptions
{
    /// <summary>Name of the configuration section bound to this type.</summary>
    public const string SectionName = "Processor";

    /// <summary>Token required by the management API in the <c>Authorization: Bearer</c> header.</summary>
    public string AdminToken { get; set; } = string.Empty;

    /// <summary>Shared secret expected in the <c>X-GlitchPort-Internal</c> header.</summary>
    public string InternalSharedSecret { get; set; } = string.Empty;

    /// <summary>Origins allowed by the dashboard CORS policy.</summary>
    public string[] CorsOrigins { get; set; } = [];
}
