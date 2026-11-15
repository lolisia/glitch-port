using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace GlitchPort
{
    /// <summary>
    /// Sends captured events to a glitch-port relay with
    /// <c>POST /api/{projectId}/events</c>. Bodies of 1024 UTF-8 bytes or more
    /// are gzip-compressed. Delivery happens on the base class worker thread; a
    /// failed attempt is retried once before it is counted as failed.
    /// </summary>
    public sealed class HttpRelay : QueuedRelay
    {
        private const string TypeName = "http";
        private const string DefaultName = "http";
        private const string ApiKeyHeaderName = "X-GlitchPort-Key";
        private const int MaxAttempts = 2;
        private const int RetryDelayMilliseconds = 200;

        /// <summary>
        /// The minimum serialized body size, in UTF-8 bytes, at which the relay
        /// compresses the request. Smaller bodies are sent as-is.
        /// </summary>
        internal const int CompressionThresholdBytes = 1024;

        private readonly HttpClient _httpClient;
        private readonly Uri _eventsEndpoint;
        private readonly string _publicKey;

        /// <summary>Creates an HTTP relay.</summary>
        /// <param name="dsn">The parsed DSN that identifies the relay and project.</param>
        /// <param name="options">The relay options.</param>
        /// <param name="httpClient">The shared <see cref="HttpClient"/> used to send events.</param>
        public HttpRelay(Dsn dsn, HttpRelayOptions options, HttpClient httpClient)
            : base(
                options is null ? DefaultName : options.Name ?? DefaultName,
                TypeName,
                options is null ? Level.Debug : options.MinLevel,
                options is null ? 1000 : options.QueueCapacity)
        {
            if (dsn is null)
            {
                throw new ArgumentNullException(nameof(dsn));
            }

            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (httpClient is null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            _httpClient = httpClient;
            _publicKey = dsn.PublicKey;
            _eventsEndpoint = new Uri(
                dsn.RelayBaseUri,
                "/api/" + dsn.ProjectId.ToString(CultureInfo.InvariantCulture) + "/events");
        }

        /// <inheritdoc/>
        protected override void Handle(CapturedEvent e)
        {
            string? lastError = null;
            var body = Encoding.UTF8.GetBytes(e.Json);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    Thread.Sleep(RetryDelayMilliseconds);
                }

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, _eventsEndpoint))
                    {
                        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, _publicKey);
                        request.Content = CreateContent(body);

                        using (var response = _httpClient
                            .SendAsync(request, HttpCompletionOption.ResponseContentRead)
                            .GetAwaiter()
                            .GetResult())
                        {
                            var responseBody = response.Content is null
                                ? null
                                : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                            if (response.IsSuccessStatusCode)
                            {
                                MarkDelivered();
                                return;
                            }

                            lastError = DescribeStatus(response.StatusCode, responseBody);
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = RelayErrorText.Describe(ex);
                }
            }

            MarkFailed(lastError ?? "The relay request failed.");
        }

        /// <summary>
        /// Creates the request content for one attempt. Large payloads are
        /// compressed with gzip and advertised with <c>Content-Encoding: gzip</c>.
        /// </summary>
        private static HttpContent CreateContent(byte[] body)
        {
            HttpContent content;

            if (body.Length >= CompressionThresholdBytes)
            {
                using (var buffer = new MemoryStream())
                {
                    using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        gzip.Write(body, 0, body.Length);
                    }

                    content = new ByteArrayContent(buffer.ToArray());
                }

                content.Headers.ContentEncoding.Add("gzip");
            }
            else
            {
                content = new ByteArrayContent(body);
            }

            content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

            return content;
        }

        private static string DescribeStatus(HttpStatusCode statusCode, string? responseBody)
        {
            var status = "HTTP "
                + ((int)statusCode).ToString(CultureInfo.InvariantCulture)
                + " ("
                + statusCode
                + ")";

            var body = RelayErrorText.Sanitize(responseBody);
            return body.Length == 0 ? status : status + ": " + body;
        }
    }
}
