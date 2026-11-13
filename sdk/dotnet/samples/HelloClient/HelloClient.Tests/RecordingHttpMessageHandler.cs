using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.HelloClient.Tests
{
    /// <summary>
    /// A tiny <see cref="HttpMessageHandler"/> that records the outgoing request and
    /// replies with a fixed response, so the sample can be exercised without a relay.
    /// </summary>
    internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public RecordingHttpMessageHandler(HttpStatusCode statusCode = HttpStatusCode.OK, string responseBody = "{}")
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? ApiKey { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Method = request.Method;
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("X-GlitchPort-Key", out var values)
                ? string.Join(",", values)
                : null;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
