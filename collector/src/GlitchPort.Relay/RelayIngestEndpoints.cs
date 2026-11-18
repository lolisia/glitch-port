using System.Net;
using System.Text.Json;
using GlitchPort.Collector.Contracts;
using GlitchPort.Relay.Config;
using GlitchPort.Relay.RateLimit;

namespace GlitchPort.Relay;

/// <summary>
/// Shared ingest pipeline used by both <c>POST /api/{projectId}/events</c> and
/// the legacy <c>POST /api/{projectId}/messages</c> endpoint: authorize, apply
/// the project and key rate limits, enforce the decompressed body limit,
/// decompress, validate, normalize, build a protocol v2 envelope and publish it
/// to the Processor.
/// </summary>
internal static class RelayIngestEndpoints
{
    /// <summary>Handles a structured protocol v1 event.</summary>
    public static Task<IResult> HandleEventAsync(
        long projectId,
        HttpContext httpContext,
        ProjectConfigProvider projectConfigs,
        ProjectRateLimiter rateLimiter,
        IEnvelopePublisher publisher,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
        HandleAsync(
            projectId,
            httpContext,
            projectConfigs,
            rateLimiter,
            publisher,
            logger,
            isLegacyMessage: false,
            cancellationToken);

    /// <summary>Handles a legacy message-only event.</summary>
    public static Task<IResult> HandleMessageAsync(
        long projectId,
        HttpContext httpContext,
        ProjectConfigProvider projectConfigs,
        ProjectRateLimiter rateLimiter,
        IEnvelopePublisher publisher,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
        HandleAsync(
            projectId,
            httpContext,
            projectConfigs,
            rateLimiter,
            publisher,
            logger,
            isLegacyMessage: true,
            cancellationToken);

    private static async Task<IResult> HandleAsync(
        long projectId,
        HttpContext httpContext,
        ProjectConfigProvider projectConfigs,
        ProjectRateLimiter rateLimiter,
        IEnvelopePublisher publisher,
        ILogger<Program> logger,
        bool isLegacyMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(projectConfigs);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(logger);

        // Without a loaded Processor project configuration there is no key to
        // authorize against. Answer before touching the body or the rate
        // limiter so the client retries instead of losing the event, and do not
        // remember the presented key: the configuration may simply not be
        // loaded yet.
        if (!projectConfigs.IsConfigurationLoaded)
        {
            logger.LogDebug(
                "Rejecting ingest request for project {ProjectId}: the project configuration is not loaded.",
                projectId);

            return RelayResults.ConfigurationNotLoaded(httpContext.Response);
        }

        var authorization = Authorize(httpContext.Request, projectId, projectConfigs);
        if (authorization is null)
        {
            // Never log the supplied key or the configured keys.
            logger.LogWarning(
                "Rejected ingest request for project {ProjectId}: missing or invalid public key.",
                projectId);

            return RelayResults.Unauthorized();
        }

        // Enforcement happens before the body is read: a rate-limited client
        // must not be able to make the Relay decompress a payload.
        var decision = rateLimiter.Acquire(
            authorization.ProjectId,
            authorization.Key,
            authorization.RateLimitPerMinute);

        if (!decision.IsAllowed)
        {
            logger.LogDebug(
                "Rate limited ingest request for project {ProjectId}; retry after {RetryAfterSeconds}s.",
                projectId,
                decision.RetryAfterSeconds);

            return RelayResults.RateLimited(
                httpContext.Response,
                decision.RetryAfterSeconds);
        }

        var body = await RequestBodyReader
            .ReadAsync(httpContext.Request, cancellationToken)
            .ConfigureAwait(false);

        switch (body.Status)
        {
            case BodyReadStatus.TooLarge:
                return RelayResults.PayloadTooLarge(
                    $"The request body must not exceed {RelayConstants.MaxRequestBodyBytes} bytes after decompression.");
            case BodyReadStatus.UnsupportedEncoding:
                return RelayResults.BadRequest(
                    "Content-Encoding: only 'gzip' is supported.");
            case BodyReadStatus.InvalidGzip:
                return RelayResults.BadRequest(
                    "Content-Encoding: the request body is not valid gzip data.");
            case BodyReadStatus.Success:
                break;
            default:
                return RelayResults.BadRequest("body: the request body could not be read");
        }

        var receivedAt = DateTimeOffset.UtcNow;
        var clientIp = GetClientIp(httpContext.Connection.RemoteIpAddress);

        var normalized = isLegacyMessage
            ? EventNormalizer.NormalizeMessage(body.Content!, receivedAt)
            : EventNormalizer.NormalizeEvent(body.Content!, clientIp, receivedAt);

        if (!normalized.IsSuccess)
        {
            return RelayResults.BadRequest(normalized.Error!);
        }

        var envelope = BuildEnvelope(
            projectId,
            httpContext,
            receivedAt,
            clientIp,
            normalized.Event!);

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

            return RelayResults.ServiceUnavailable(httpContext.Response);
        }

        return Results.Json(
            new IngestResponse { Id = envelope.EventId },
            RelayJsonContext.Default.IngestResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static Envelope BuildEnvelope(
        long projectId,
        HttpContext httpContext,
        DateTimeOffset receivedAt,
        string? clientIp,
        NormalizedEvent normalized) =>
        new()
        {
            Version = 2,
            EventId = normalized.EventId,
            ProjectId = projectId,
            ReceivedAt = receivedAt,
            Endpoint = string.Concat(
                httpContext.Request.Method,
                " ",
                httpContext.Request.Path),
            Message = normalized.Message,
            ClientTimestamp = normalized.ClientTimestamp,
            SdkName = normalized.SdkName,
            SdkVersion = normalized.SdkVersion,
            Payload = JsonSerializer.SerializeToElement(
                normalized.Payload,
                RelayJsonContext.Default.EventV1Payload),
            ClientIp = clientIp,
        };

    private static string? GetClientIp(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();
    }

    private static PublicKeyAuthorization? Authorize(
        HttpRequest request,
        long projectId,
        ProjectConfigProvider projectConfigs)
    {
        if (!request.Headers.TryGetValue(RelayConstants.PublicKeyHeader, out var headerValues))
        {
            return null;
        }

        var providedKey = headerValues.ToString();
        if (string.IsNullOrEmpty(providedKey))
        {
            return null;
        }

        return projectConfigs.Authorize(projectId, providedKey);
    }
}
