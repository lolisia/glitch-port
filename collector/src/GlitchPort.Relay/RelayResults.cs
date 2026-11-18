using System.Globalization;

namespace GlitchPort.Relay;

/// <summary>Creates the JSON results returned by the Relay endpoints.</summary>
internal static class RelayResults
{
    private const string UnauthorizedMessage = "A valid X-GlitchPort-Key header is required.";
    private const string RateLimitedMessage = "rate limit exceeded";
    private const string ConfigurationNotLoadedMessage = "project configuration not loaded";
    private const string ServiceUnavailableMessage =
        "The event could not be published. Please retry later.";

    /// <summary>A 401 response for a missing or invalid public key.</summary>
    public static IResult Unauthorized() =>
        Error(StatusCodes.Status401Unauthorized, UnauthorizedMessage);

    /// <summary>
    /// A 429 response for a request rejected by the rate limiter, including the
    /// <c>Retry-After</c> header clients should honour before retrying.
    /// </summary>
    public static IResult RateLimited(HttpResponse response, int retryAfterSeconds)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers["Retry-After"] = Math.Max(1, retryAfterSeconds).ToString(
            CultureInfo.InvariantCulture);

        return Error(StatusCodes.Status429TooManyRequests, RateLimitedMessage);
    }

    /// <summary>A 400 response for an invalid request.</summary>
    public static IResult BadRequest(string message) =>
        Error(StatusCodes.Status400BadRequest, message);

    /// <summary>A 413 response for a body that exceeds the decompressed size limit.</summary>
    public static IResult PayloadTooLarge(string message) =>
        Error(StatusCodes.Status413PayloadTooLarge, message);

    /// <summary>
    /// A 503 response returned before the first successful Processor project
    /// configuration load, including the <c>Retry-After</c> header clients should
    /// honour before retrying.
    /// </summary>
    public static IResult ConfigurationNotLoaded(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers["Retry-After"] = RelayConstants.RetryAfterSeconds.ToString(
            CultureInfo.InvariantCulture);

        return Error(
            StatusCodes.Status503ServiceUnavailable,
            ConfigurationNotLoadedMessage);
    }

    /// <summary>
    /// A 503 response for a failed publish to the Processor, including the
    /// <c>Retry-After</c> header clients should honour before retrying.
    /// </summary>
    public static IResult ServiceUnavailable(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers["Retry-After"] = RelayConstants.RetryAfterSeconds.ToString(
            CultureInfo.InvariantCulture);

        return Error(StatusCodes.Status503ServiceUnavailable, ServiceUnavailableMessage);
    }

    private static IResult Error(int statusCode, string message) =>
        Results.Json(
            new ErrorResponse { Error = message },
            RelayJsonContext.Default.ErrorResponse,
            statusCode: statusCode);
}
