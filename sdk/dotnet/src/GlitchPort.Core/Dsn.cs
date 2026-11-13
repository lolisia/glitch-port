using System;
using System.Globalization;

namespace GlitchPort
{
    /// <summary>
    /// A parsed Data Source Name (DSN) that identifies a glitch-port project and
    /// the relay that accepts its events.
    /// </summary>
    /// <remarks>
    /// The accepted form is <c>http://{publicKey}@{host}:{port}/{projectId}</c>; the
    /// port segment is optional. The public key is the URI userinfo and the project
    /// id is the last path segment.
    /// </remarks>
    public sealed class Dsn
    {
        private Dsn(string publicKey, long projectId, Uri relayBaseUri)
        {
            PublicKey = publicKey;
            ProjectId = projectId;
            RelayBaseUri = relayBaseUri;
        }

        /// <summary>Gets the public key that authenticates events for the project.</summary>
        public string PublicKey { get; }

        /// <summary>Gets the numeric project id.</summary>
        public long ProjectId { get; }

        /// <summary>
        /// Gets the relay base URI built from the DSN's scheme, host and port. The
        /// public key userinfo and any path prefix are stripped, and the underlying
        /// authority string (see <see cref="Uri.OriginalString"/>) has no trailing
        /// slash, so ingest endpoints can be appended as absolute paths (for example
        /// <c>/api/{projectId}/messages</c>).
        /// </summary>
        public Uri RelayBaseUri { get; }

        /// <summary>Parses a DSN.</summary>
        /// <param name="dsn">The DSN string.</param>
        /// <returns>The parsed DSN.</returns>
        /// <exception cref="FormatException">The DSN is missing, malformed, or lacks a required component.</exception>
        public static Dsn Parse(string dsn)
        {
            if (string.IsNullOrWhiteSpace(dsn))
            {
                throw new FormatException("A DSN is required.");
            }

            var uri = CreateAbsoluteUri(dsn.Trim());

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("The DSN must use the http or https scheme.");
            }

            if (string.IsNullOrEmpty(uri.Host))
            {
                throw new FormatException("The DSN must include a host.");
            }

            var publicKey = uri.UserInfo;
            if (string.IsNullOrEmpty(publicKey))
            {
                throw new FormatException("The DSN must include a public key as the URI userinfo component.");
            }

            var projectId = ParseProjectId(uri);

            // Only the scheme, host and port belong in the relay base URI: neither the
            // public key userinfo nor any path prefix/query string may leak into the
            // ingest endpoint.
            var authority = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
            var relayBaseUri = new Uri(authority, UriKind.Absolute);

            return new Dsn(publicKey, projectId, relayBaseUri);
        }

        /// <summary>Attempts to parse a DSN without throwing.</summary>
        /// <param name="dsn">The DSN string.</param>
        /// <param name="value">The parsed DSN, or <see langword="null"/> when parsing fails.</param>
        /// <returns><see langword="true"/> when <paramref name="dsn"/> is a valid DSN; otherwise <see langword="false"/>.</returns>
        public static bool TryParse(string? dsn, out Dsn value)
        {
            if (string.IsNullOrWhiteSpace(dsn))
            {
                value = null!;
                return false;
            }

            try
            {
                value = Parse(dsn);
                return true;
            }
            catch (FormatException)
            {
                value = null!;
                return false;
            }
        }

        private static Uri CreateAbsoluteUri(string dsn)
        {
            try
            {
                if (!Uri.TryCreate(dsn, UriKind.Absolute, out var uri) || uri is null)
                {
                    throw new FormatException("The DSN is not a valid absolute URI.");
                }

                return uri;
            }
            catch (UriFormatException ex)
            {
                throw new FormatException("The DSN is not a valid absolute URI.", ex);
            }
        }

        private static long ParseProjectId(Uri uri)
        {
            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                throw new FormatException("The DSN must include a project id as its last path segment.");
            }

            var lastSegment = Uri.UnescapeDataString(segments[segments.Length - 1]);
            if (!long.TryParse(lastSegment, NumberStyles.None, CultureInfo.InvariantCulture, out var projectId))
            {
                throw new FormatException("The DSN project id is not a valid integer.");
            }

            return projectId;
        }
    }
}
