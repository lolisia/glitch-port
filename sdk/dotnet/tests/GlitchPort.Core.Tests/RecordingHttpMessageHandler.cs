using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.Tests
{
    /// <summary>
    /// A tiny <see cref="HttpMessageHandler"/> that records the outgoing request and
    /// replies with a fixed response, so tests can assert on exactly what the SDK sent.
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

        public string? ContentType { get; private set; }

        /// <summary>Gets the <c>Content-Encoding</c> header value, or <see langword="null"/>.</summary>
        public string? ContentEncoding { get; private set; }

        /// <summary>Gets the exact bytes of the request body, without any decompression.</summary>
        public byte[]? BodyBytes { get; private set; }

        /// <summary>
        /// Gets the request body decoded as UTF-8. Compressed bodies are recorded
        /// verbatim in <see cref="BodyBytes"/> and therefore decode to garbage; use
        /// the raw bytes and decompress them instead.
        /// </summary>
        public string? Body { get; private set; }

        public bool Disposed { get; private set; }

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
            ContentType = request.Content?.Headers.ContentType?.ToString();
            ContentEncoding = request.Content is not null && request.Content.Headers.ContentEncoding.Count > 0
                ? string.Join(",", request.Content.Headers.ContentEncoding)
                : null;
            BodyBytes = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            Body = BodyBytes is null ? null : Encoding.UTF8.GetString(BodyBytes);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
