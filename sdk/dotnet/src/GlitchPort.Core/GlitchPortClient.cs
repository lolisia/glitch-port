using System;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort
{
    /// <summary>Captures messages and sends them to a glitch-port relay.</summary>
    public sealed class GlitchPortClient : IDisposable
    {
        private const string ApiKeyHeaderName = "X-GlitchPort-Key";
        private const string FallbackSdkVersion = "0.0.0";

        private static readonly string InformationalVersion = ResolveInformationalVersion();

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly Uri _messagesEndpoint;
        private readonly string _publicKey;
        private readonly string _sdkName;
        private readonly string _sdkVersion;
        private bool _disposed;

        /// <summary>Creates a client for the configured project.</summary>
        /// <param name="options">The client options. <see cref="GlitchPortOptions.Dsn"/> is required.</param>
        /// <param name="httpClient">
        /// An optional <see cref="HttpClient"/>. When supplied, the client uses it and
        /// never disposes it; otherwise the client creates and owns a private instance.
        /// </param>
        public GlitchPortClient(GlitchPortOptions options, HttpClient? httpClient = null)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var dsn = options.Dsn;
            if (dsn is null)
            {
                throw new ArgumentException("The DSN must be configured on the options instance.", nameof(options));
            }

            _publicKey = dsn.PublicKey;
            _sdkName = string.IsNullOrWhiteSpace(options.SdkName) ? GlitchPortOptions.DefaultSdkName : options.SdkName;
            _sdkVersion = string.IsNullOrWhiteSpace(options.SdkVersion) ? InformationalVersion : options.SdkVersion;

            var projectId = dsn.ProjectId.ToString(CultureInfo.InvariantCulture);
            _messagesEndpoint = new Uri(
                dsn.RelayBaseUri,
                "/api/" + projectId + "/messages");

            if (httpClient is null)
            {
                _httpClient = new HttpClient();
                _ownsHttpClient = true;
            }
            else
            {
                _httpClient = httpClient;
                _ownsHttpClient = false;
            }
        }

        /// <summary>Captures a message and sends it to the relay.</summary>
        /// <param name="message">The message text.</param>
        /// <param name="ct">A token that cancels the request.</param>
        /// <returns>The generated event id.</returns>
        /// <exception cref="GlitchPortException">The relay responded with a non-success status code.</exception>
        public async Task<Guid> CaptureMessageAsync(string message, CancellationToken ct = default)
        {
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            var eventId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var payload = JsonPayloadWriter.CreateMessagePayload(eventId, timestamp, message, _sdkName, _sdkVersion);

            using var request = new HttpRequestMessage(HttpMethod.Post, _messagesEndpoint);
            request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, _publicKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var responseBody = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new GlitchPortException(
                    "The glitch-port relay responded with HTTP "
                        + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                        + " ("
                        + response.StatusCode
                        + ").",
                    response.StatusCode,
                    responseBody,
                    response.RequestMessage?.RequestUri ?? _messagesEndpoint);
            }

            return eventId;
        }

        /// <summary>
        /// Releases resources owned by the client. An injected <see cref="HttpClient"/>
        /// is left untouched.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GlitchPortClient));
            }
        }

        private static string ResolveInformationalVersion()
        {
            var informationalVersion = typeof(GlitchPortClient).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            return string.IsNullOrWhiteSpace(informationalVersion) ? FallbackSdkVersion : informationalVersion;
        }
    }
}
