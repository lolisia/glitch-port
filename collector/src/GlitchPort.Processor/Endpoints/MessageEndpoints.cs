using GlitchPort.Processor.Data;
using GlitchPort.Processor.Security;
using Microsoft.Extensions.Options;

namespace GlitchPort.Processor.Endpoints;

/// <summary>
/// Management endpoints consumed by the dashboard.
/// </summary>
internal static class MessageEndpoints
{
    private const int DefaultLimit = 100;
    private const int MinLimit = 1;
    private const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/manage/messages", ListMessagesAsync);
        return endpoints;
    }

    private static async Task<IResult> ListMessagesAsync(
        HttpRequest request,
        int? limit,
        MessageStore store,
        IOptions<ProcessorOptions> options,
        CancellationToken cancellationToken)
    {
        if (!SecretComparer.MatchesBearerToken(
                request.Headers.Authorization.ToString(),
                options.Value.AdminToken))
        {
            return Results.Unauthorized();
        }

        var effectiveLimit = limit ?? DefaultLimit;
        if (effectiveLimit is < MinLimit or > MaxLimit)
        {
            return Results.BadRequest();
        }

        var messages = await store.ListAsync(effectiveLimit, cancellationToken).ConfigureAwait(false);

        return Results.Json(messages, ProcessorJsonContext.Default.ListMessageListItem);
    }
}
