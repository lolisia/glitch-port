using System.Text.Json;
using GlitchPort.Collector.Contracts;
using GlitchPort.Relay;
using GlitchPort.Transport.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
    RelayOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<RelayOptions>();
    return new HttpClient { Timeout = options.Timeout };
});

// Registered as a singleton so tests can swap the publisher through
// ConfigureTestServices without touching the transport implementation.
builder.Services.AddSingleton<IEnvelopePublisher>(sp =>
{
    var options = sp.GetRequiredService<RelayOptions>();
    return new HttpEnvelopePublisher(
        new HttpTransportOptions
        {
            Endpoints = options.ProcessorEndpoints,
            SharedSecret = options.InternalSharedSecret,
            Timeout = options.Timeout,
        },
        sp.GetRequiredService<HttpClient>());
});

var app = builder.Build();

app.MapGet(
    "/healthz",
    static () => Results.Json(
        new HealthResponse { Status = "ok" },
        RelayJsonContext.Default.HealthResponse));

app.MapPost("/api/{projectId:long}/messages", HandleIngestAsync);

app.Run();

static async Task<IResult> HandleIngestAsync(
    long projectId,
    HttpContext httpContext,
    RelayOptions options,
    IEnvelopePublisher publisher,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    if (!HasValidPublicKey(httpContext.Request, projectId, options))
    {
        // Never log the supplied key or the configured keys.
        logger.LogWarning(
            "Rejected ingest request for project {ProjectId}: missing or invalid public key.",
            projectId);

        return RelayResults.Unauthorized();
    }

    IngestMessageRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync(
            httpContext.Request.Body,
            RelayJsonContext.Default.IngestMessageRequest,
            cancellationToken).ConfigureAwait(false);
    }
    catch (JsonException)
    {
        return RelayResults.BadRequest("The request body is not valid JSON for this endpoint.");
    }

    if (request is null)
    {
        return RelayResults.BadRequest("The request body is required.");
    }

    var message = request.Message;
    if (string.IsNullOrWhiteSpace(message))
    {
        return RelayResults.BadRequest("The 'message' field is required and must not be blank.");
    }

    if (message.Length > RelayConstants.MaxMessageLength)
    {
        return RelayResults.BadRequest(
            $"The 'message' field must be at most {RelayConstants.MaxMessageLength} characters.");
    }

    var envelope = new Envelope
    {
        Version = 1,
        EventId = request.EventId ?? Guid.NewGuid(),
        ProjectId = projectId,
        ReceivedAt = DateTimeOffset.UtcNow,
        Endpoint = string.Concat(httpContext.Request.Method, " ", httpContext.Request.Path),
        Message = message.Trim(),
        ClientTimestamp = request.Timestamp,
        SdkName = request.Sdk?.Name,
        SdkVersion = request.Sdk?.Version,
    };

    try
    {
        await publisher.PublishAsync([envelope], cancellationToken).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
        logger.LogWarning(
            exception,
            "Failed to publish event {EventId} for project {ProjectId}.",
            envelope.EventId,
            projectId);

        return RelayResults.ServiceUnavailable();
    }

    return Results.Json(
        new IngestMessageResponse { Id = envelope.EventId },
        RelayJsonContext.Default.IngestMessageResponse,
        statusCode: StatusCodes.Status202Accepted);
}

static bool HasValidPublicKey(HttpRequest request, long projectId, RelayOptions options)
{
    if (!request.Headers.TryGetValue(RelayConstants.PublicKeyHeader, out var headerValues))
    {
        return false;
    }

    var providedKey = headerValues.ToString();
    if (string.IsNullOrEmpty(providedKey))
    {
        return false;
    }

    return options.Projects.TryGetValue(projectId, out var projectKeys)
        && projectKeys is not null
        && projectKeys.Contains(providedKey, StringComparer.Ordinal);
}

public partial class Program { }
