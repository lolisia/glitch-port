using System.Text.Json;
using GlitchPort.Collector.Contracts;
using GlitchPort.Processor.Data;
using GlitchPort.Processor.Security;
using Microsoft.Extensions.Options;

namespace GlitchPort.Processor.Endpoints;

/// <summary>
/// Internal endpoints called by Relay instances.
/// </summary>
internal static class EnvelopeEndpoints
{
    private const string InternalSecretHeader = "X-GlitchPort-Internal";

    public static IEndpointRouteBuilder MapEnvelopeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/envelopes", StoreEnvelopesAsync);
        return endpoints;
    }

    private static async Task<IResult> StoreEnvelopesAsync(
        HttpRequest request,
        MessageStore store,
        IOptions<ProcessorOptions> options,
        CancellationToken cancellationToken)
    {
        var providedSecret = request.Headers[InternalSecretHeader].ToString();
        if (!SecretComparer.FixedTimeEquals(providedSecret, options.Value.InternalSharedSecret))
        {
            return Results.Unauthorized();
        }

        Envelope[]? envelopes;
        try
        {
            envelopes = await JsonSerializer
                .DeserializeAsync(request.Body, ContractsJsonContext.Default.EnvelopeArray, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (envelopes is null)
        {
            return Results.BadRequest();
        }

        var stored = await store.InsertAsync(envelopes, cancellationToken).ConfigureAwait(false);

        return Results.Json(
            new StoredEnvelopesResponse(stored),
            ProcessorJsonContext.Default.StoredEnvelopesResponse);
    }
}
