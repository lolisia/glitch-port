using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace GlitchPort
{
    /// <summary>
    /// Sends captured events to a glitch-port relay with
    /// <c>POST /api/{projectId}/messages</c>. Delivery happens on the base class
    /// worker thread; a failed attempt is retried once before it is counted as
    /// failed.
    /// </summary>
    public sealed class HttpRelay : QueuedRelay
    {
        private const string TypeName = "http";
        private const string DefaultName = "http";
        private const string ApiKeyHeaderName = "X-GlitchPort-Key";
        private const int MaxAttempts = 2;
        private const int RetryDelayMilliseconds = 200;

        private readonly HttpClient _httpClient;
        private readonly Uri _messagesEndpoint;
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
            _messagesEndpoint = new Uri(
                dsn.RelayBaseUri,
                "/api/" + dsn.ProjectId.ToString(CultureInfo.InvariantCulture) + "/messages");
        }

        /// <inheritdoc/>
        protected override void Handle(CapturedEvent e)
        {
            string? lastError = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    Thread.Sleep(RetryDelayMilliseconds);
                }

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, _messagesEndpoint))
                    {
                        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, _publicKey);
                        request.Content = new StringContent(e.Json, Encoding.UTF8, "application/json");

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
