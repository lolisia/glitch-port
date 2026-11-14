using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Issue event list, event detail and event delete endpoints.
/// </summary>
internal static class ManagementEventEndpoints
{
    /// <summary>Maps the event endpoints on <paramref name="group"/>.</summary>
    public static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/issues/{issueId:long}/events", ListIssueEventsAsync);
        group.MapGet("/events/{eventId:guid}", GetEventAsync);
        group.MapDelete("/events/{eventId:guid}", DeleteEventAsync);

        return group;
    }

    /// <summary>
    /// Lists the events of an issue, newest first.
    /// </summary>
    private static async Task<IResult> ListIssueEventsAsync(
        long issueId,
        string? limit,
        string? cursor,
        ManagementEventStore events,
        CancellationToken cancellationToken)
    {
        if (!ManagementValidation.TryResolveLimit(limit, out var pageSize))
        {
            return ManagementHttp.BadRequest("limit must be between 1 and 100.");
        }

        EventCursor? decodedCursor = null;
        if (cursor is not null)
        {
            if (!ManagementValidation.TryDecodeEventCursor(cursor, out var value))
            {
                return ManagementHttp.BadRequest("cursor is malformed.");
            }

            decodedCursor = value;
        }

        if (!await events.IssueExistsAsync(issueId, cancellationToken).ConfigureAwait(false))
        {
            return ManagementHttp.NotFound($"Issue {issueId} was not found.");
        }

        var page = await events.ListAsync(issueId, pageSize, decodedCursor, cancellationToken)
            .ConfigureAwait(false);

        return ManagementHttp.Json(page, ProcessorJsonContext.Default.EventListResponse);
    }

    private static async Task<IResult> GetEventAsync(
        Guid eventId,
        ManagementEventStore events,
        CancellationToken cancellationToken)
    {
        var storedEvent = await events.GetAsync(eventId, cancellationToken).ConfigureAwait(false);

        return storedEvent is null
            ? ManagementHttp.NotFound($"Event {eventId} was not found.")
            : ManagementHttp.Json(storedEvent, ProcessorJsonContext.Default.EventDetailResponse);
    }

    private static async Task<IResult> DeleteEventAsync(
        Guid eventId,
        ManagementEventStore events,
        CancellationToken cancellationToken)
    {
        var deleted = await events.DeleteAsync(eventId, cancellationToken).ConfigureAwait(false);

        return deleted
            ? Results.NoContent()
            : ManagementHttp.NotFound($"Event {eventId} was not found.");
    }
}
