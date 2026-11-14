using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Result and request-body helpers shared by the management endpoints.
/// </summary>
internal static class ManagementHttp
{
    /// <summary>Returns a JSON response serialized with the Processor context.</summary>
    public static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, typeInfo, statusCode: statusCode);

    /// <summary>Returns <c>400 Bad Request</c> with an <c>error</c> body.</summary>
    public static IResult BadRequest(string error) => Error(StatusCodes.Status400BadRequest, error);

    /// <summary>Returns <c>404 Not Found</c> with an <c>error</c> body.</summary>
    public static IResult NotFound(string error) => Error(StatusCodes.Status404NotFound, error);

    /// <summary>Returns <c>409 Conflict</c> with an <c>error</c> body.</summary>
    public static IResult Conflict(string error) => Error(StatusCodes.Status409Conflict, error);

    /// <summary>
    /// Reads a JSON request body.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the body is missing or is not valid JSON
    /// for <typeparamref name="T"/>; the caller answers <c>400</c>.
    /// </returns>
    public static async Task<T?> ReadJsonAsync<T>(
        HttpRequest request,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await request.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IResult Error(int statusCode, string error) =>
        Results.Json(
            new ManagementErrorResponse(error),
            ProcessorJsonContext.Default.ManagementErrorResponse,
            statusCode: statusCode);
}
