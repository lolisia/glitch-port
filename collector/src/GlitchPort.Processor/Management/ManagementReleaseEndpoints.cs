using System.Globalization;
using GlitchPort.Processor.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Release list endpoint of a project.
/// </summary>
/// <remarks>
/// The aggregate is computed from the stored events, so it only covers events
/// still inside the retention window: events removed by retention no longer
/// contribute to a release.
/// </remarks>
internal static class ManagementReleaseEndpoints
{
    /// <summary>Page size used when a request does not specify <c>limit</c>.</summary>
    private const int DefaultLimit = 50;

    /// <summary>Largest accepted <c>limit</c>.</summary>
    private const int MaxLimit = 100;

    /// <summary>Maps the release endpoints on <paramref name="group"/>.</summary>
    public static RouteGroupBuilder MapReleaseEndpoints(
        this RouteGroupBuilder group,
        ManagementReleaseStore store)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(store);

        group.MapGet("/projects/{projectId:long}/releases", (
            long projectId,
            string? environment,
            string? limit,
            string? cursor,
            ProjectStore projects,
            CancellationToken cancellationToken) =>
            ListReleasesAsync(store, projectId, environment, limit, cursor, projects, cancellationToken));

        return group;
    }

    private static async Task<IResult> ListReleasesAsync(
        ManagementReleaseStore store,
        long projectId,
        string? environment,
        string? limit,
        string? cursor,
        ProjectStore projects,
        CancellationToken cancellationToken)
    {
        if (!await projects.ExistsAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return ManagementHttp.NotFound($"Project {projectId} was not found.");
        }

        if (!TryResolveLimit(limit, out var pageSize))
        {
            return ManagementHttp.BadRequest("limit must be between 1 and 100.");
        }

        ReleaseCursor? decodedCursor = null;
        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var value))
            {
                return ManagementHttp.BadRequest("cursor is malformed.");
            }

            decodedCursor = value;
        }

        var page = await store
            .ListAsync(
                projectId,
                ManagementValidation.NormalizeFilter(environment),
                pageSize,
                decodedCursor,
                cancellationToken)
            .ConfigureAwait(false);

        return ManagementHttp.Json(page, ProcessorJsonContext.Default.ReleaseListResponse);
    }

    /// <summary>
    /// Validates the release <c>limit</c>, applying the default of 50.
    /// </summary>
    private static bool TryResolveLimit(string? value, out int pageSize)
    {
        pageSize = DefaultLimit;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed is < 1 or > MaxLimit)
        {
            return false;
        }

        pageSize = parsed;
        return true;
    }

    /// <summary>
    /// Decodes a release list cursor. Its first value is the ISO 8601
    /// <c>last_seen</c> of the last row and its second value is the release
    /// name.
    /// </summary>
    private static bool TryDecodeCursor(string raw, out ReleaseCursor cursor)
    {
        cursor = default;

        if (!ManagementCursor.TryDecode(raw, out var lastSeenText, out var release))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                lastSeenText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var lastSeen))
        {
            return false;
        }

        cursor = new ReleaseCursor(lastSeen, release);
        return true;
    }
}
