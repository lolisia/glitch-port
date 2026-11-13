namespace GlitchPort.Relay;

/// <summary>Creates the JSON results returned by the Relay endpoints.</summary>
internal static class RelayResults
{
    private const string UnauthorizedMessage = "A valid X-GlitchPort-Key header is required.";
    private const string ServiceUnavailableMessage =
        "The event could not be published. Please retry later.";

    /// <summary>A 401 response for a missing or invalid public key.</summary>
    public static IResult Unauthorized() =>
        Error(StatusCodes.Status401Unauthorized, UnauthorizedMessage);

    /// <summary>A 400 response for an invalid request.</summary>
    public static IResult BadRequest(string message) =>
        Error(StatusCodes.Status400BadRequest, message);

    /// <summary>A 503 response for a failed publish to the Processor.</summary>
    public static IResult ServiceUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, ServiceUnavailableMessage);

    private static IResult Error(int statusCode, string message) =>
        Results.Json(
            new ErrorResponse { Error = message },
            RelayJsonContext.Default.ErrorResponse,
            statusCode: statusCode);
}
