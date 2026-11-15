using GlitchPort.Processor.Projects;

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

    /// <summary>
    /// Public base URL of the Relay (config key <c>Processor:PublicRelayUrl</c>),
    /// for example <c>http://localhost:5080</c>. It is the base of the DSN built
    /// by <see cref="DsnBuilder"/> for every project key.
    /// </summary>
    public string PublicRelayUrl { get; set; } = string.Empty;

    /// <summary>Origins allowed by the dashboard CORS policy.</summary>
    public string[] CorsOrigins { get; set; } = [];

    /// <summary>Projects and public keys created when the Processor starts.</summary>
    public ProjectBootstrapOptions Bootstrap { get; set; } = new();
}
