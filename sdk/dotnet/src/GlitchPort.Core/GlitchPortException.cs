using System;
using System.Net;

namespace GlitchPort
{
    /// <summary>Thrown when the relay rejects a captured event with a non-success HTTP status.</summary>
    public sealed class GlitchPortException : Exception
    {
        /// <summary>Creates a new exception describing a failed relay request.</summary>
        /// <param name="message">The human-readable error message.</param>
        /// <param name="statusCode">The HTTP status code returned by the relay.</param>
        /// <param name="responseBody">The response body returned by the relay, if any.</param>
        /// <param name="requestUri">The URI of the failed request, when available.</param>
        /// <param name="innerException">An optional inner exception.</param>
        public GlitchPortException(
            string message,
            HttpStatusCode statusCode,
            string? responseBody,
            Uri? requestUri,
            Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? string.Empty;
            RequestUri = requestUri;
        }

        /// <summary>Gets the HTTP status code returned by the relay.</summary>
        public HttpStatusCode StatusCode { get; }

        /// <summary>Gets the response body returned by the relay.</summary>
        public string ResponseBody { get; }

        /// <summary>Gets the URI of the failed request, when available.</summary>
        public Uri? RequestUri { get; }
    }
}
