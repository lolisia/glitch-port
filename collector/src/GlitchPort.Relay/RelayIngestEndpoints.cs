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
/// decompress, validate, normalize, apply the project's inbound filters, build
/// a protocol v2 envelope and publish it to the Processor.
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

        var normalizedEvent = normalized.Event!;

        // Filtering runs after normalization so the filters see the same
        // trimmed values the Processor will store, and before publishing so a
        // filtered event never leaves the Relay. The legacy messages endpoint
        // applies only the message filter.
        var filter = MatchInboundFilter(
            projectConfigs,
            projectId,
            normalizedEvent,
            isLegacyMessage);

        if (filter is not null)
        {
            logger.LogDebug(
                "Filtered event {EventId} for project {ProjectId}: the {Filter} filter matched.",
                normalizedEvent.EventId,
                projectId,
                filter);

            return RelayResults.Filtered(httpContext.Response, normalizedEvent.EventId, filter);
        }

        var envelope = BuildEnvelope(
            projectId,
            httpContext,
            receivedAt,
            clientIp,
            normalizedEvent);

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

    /// <summary>
    /// Returns the name of the first inbound filter that matches
    /// <paramref name="normalized"/>, or <see langword="null"/> when the event
    /// must be published. Release and environment filters are skipped for the
    /// legacy messages endpoint, which applies only the message filter.
    /// </summary>
    private static string? MatchInboundFilter(
        ProjectConfigProvider projectConfigs,
        long projectId,
        NormalizedEvent normalized,
        bool isLegacyMessage)
    {
        if (!projectConfigs.Snapshot.TryGetInboundFilters(projectId, out var filters))
        {
            return null;
        }

        if (!isLegacyMessage)
        {
            if (filters.MatchesRelease(normalized.Payload.Release))
            {
                return RelayConstants.FilteredRelease;
            }

            if (filters.MatchesEnvironment(normalized.Payload.Environment))
            {
                return RelayConstants.FilteredEnvironment;
            }
        }

        if (filters.MatchesMessage(normalized.Payload.Message))
        {
            return RelayConstants.FilteredMessage;
        }

        if (normalized.Payload.Exception is { Length: > 0 } exceptions)
        {
            foreach (var exception in exceptions)
            {
                if (filters.MatchesException(exception.Type, exception.Value))
                {
                    return RelayConstants.FilteredMessage;
                }
            }
        }

        return null;
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
