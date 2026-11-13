namespace GlitchPort.Transport.Http;

/// <summary>
/// Configuration for <see cref="HttpEnvelopePublisher"/>.
/// </summary>
public sealed class HttpTransportOptions
{
    /// <summary>
    /// Base URLs of the Processor instances. Publishing starts at the next
    /// endpoint in rotation and fails over to the following endpoints.
    /// </summary>
    public string[] Endpoints { get; set; } = [];

    /// <summary>Shared secret sent in the internal authentication header.</summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>Timeout applied to each individual publish attempt.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
