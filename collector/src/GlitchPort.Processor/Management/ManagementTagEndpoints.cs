using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Tag distribution endpoint of an issue.
/// </summary>
internal static class ManagementTagEndpoints
{
    /// <summary>Configuration key of the number of events the endpoint samples.</summary>
    public const string SampleLimitConfigurationKey = "Processor:Management:TagSampleLimit";

    /// <summary>Number of values returned per facet when <c>limit</c> is absent.</summary>
    private const int DefaultLimit = 10;

    /// <summary>Largest accepted number of values per facet.</summary>
    private const int MaxLimit = 25;

    /// <summary>Maps the tag endpoints on <paramref name="group"/>.</summary>
    public static RouteGroupBuilder MapTagEndpoints(
        this RouteGroupBuilder group,
        ManagementTagStore store)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(store);

        group.MapGet("/issues/{issueId:long}/tags", (
            long issueId,
            string? limit,
            CancellationToken cancellationToken) =>
            GetTagsAsync(store, issueId, limit, cancellationToken));

        return group;
    }

    private static async Task<IResult> GetTagsAsync(
        ManagementTagStore store,
        long issueId,
        string? limit,
        CancellationToken cancellationToken)
    {
        if (!TryResolveLimit(limit, out var topLimit))
        {
            return ManagementHttp.BadRequest("limit must be between 1 and 25.");
        }

        var tags = await store.GetAsync(issueId, topLimit, cancellationToken).ConfigureAwait(false);

        return tags is null
            ? ManagementHttp.NotFound($"Issue {issueId} was not found.")
            : ManagementHttp.Json(tags, ProcessorJsonContext.Default.IssueTagsResponse);
    }

    /// <summary>
    /// Validates the tag <c>limit</c>, applying the default of 10.
    /// </summary>
    private static bool TryResolveLimit(string? value, out int limit)
    {
        limit = DefaultLimit;

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

        limit = parsed;
        return true;
    }
}
