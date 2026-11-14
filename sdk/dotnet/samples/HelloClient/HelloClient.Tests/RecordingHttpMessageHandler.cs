using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.HelloClient.Tests
{
    /// <summary>
    /// A tiny <see cref="HttpMessageHandler"/> that records every outgoing request
    /// and replies with a fixed response, so the sample can be exercised without a
    /// relay.
    /// </summary>
    internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly object _gate = new object();
        private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public RecordingHttpMessageHandler(HttpStatusCode statusCode = HttpStatusCode.OK, string responseBody = "{}")
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public HttpMethod? Method => Last?.Method;

        public Uri? RequestUri => Last?.RequestUri;

        public string? ApiKey => Last?.ApiKey;

        public string? Body => Last?.Body;

        private RecordedRequest? Last
        {
            get
            {
                lock (_gate)
                {
                    return _requests.Count == 0 ? null : _requests[_requests.Count - 1];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var recorded = new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.TryGetValues("X-GlitchPort-Key", out var values)
                    ? string.Join(",", values)
                    : null,
                body);

            lock (_gate)
            {
                _requests.Add(recorded);
            }

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }

    /// <summary>One request observed by <see cref="RecordingHttpMessageHandler"/>.</summary>
    internal sealed class RecordedRequest
    {
        public RecordedRequest(HttpMethod method, Uri? requestUri, string? apiKey, string? body)
        {
            Method = method;
            RequestUri = requestUri;
            ApiKey = apiKey;
            Body = body;
        }

        public HttpMethod Method { get; }

        public Uri? RequestUri { get; }

        public string? ApiKey { get; }

        public string? Body { get; }
    }
}
