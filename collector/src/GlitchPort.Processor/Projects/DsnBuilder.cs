using System.Globalization;

namespace GlitchPort.Processor.Projects;

/// <summary>
/// Builds the DSN that clients use to send events for a project.
/// </summary>
public static class DsnBuilder
{
    /// <summary>
    /// Builds <c>http(s)://&lt;publicKey&gt;@&lt;host&gt;[:port]/&lt;projectId&gt;</c> from
    /// the public Relay URL.
    /// </summary>
    /// <param name="publicRelayUrl">
    /// Absolute <c>http</c> or <c>https</c> base URL of the public Relay, for
    /// example <c>http://localhost:5080</c>.
    /// </param>
    /// <param name="publicKey">Public key of the project.</param>
    /// <param name="projectId">Id of the project, appended as the DSN path.</param>
    /// <returns>The DSN, without a trailing slash.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="publicRelayUrl"/> is not an absolute http(s) URL, or
    /// <paramref name="publicKey"/> is blank.
    /// </exception>
    public static string Build(string publicRelayUrl, string publicKey, long projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicRelayUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projectId);

        if (!Uri.TryCreate(publicRelayUrl, UriKind.Absolute, out var relayUri)
            || (relayUri.Scheme != Uri.UriSchemeHttp && relayUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"The public Relay URL '{publicRelayUrl}' must be an absolute http or https URL.",
                nameof(publicRelayUrl));
        }

        // Building the URI through UriBuilder keeps the scheme, host and port
        // of the configured Relay URL and replaces everything else, so the
        // result never carries a trailing slash or inherited credentials.
        var dsn = new UriBuilder(relayUri)
        {
            UserName = publicKey,
            Password = string.Empty,
            Path = projectId.ToString(CultureInfo.InvariantCulture),
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return dsn.Uri.ToString();
    }
}
