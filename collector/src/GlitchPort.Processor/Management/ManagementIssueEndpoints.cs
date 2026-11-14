using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Issue list, detail, update, delete and bulk endpoints.
/// </summary>
internal static class ManagementIssueEndpoints
{
    /// <summary>Maps the issue endpoints on <paramref name="group"/>.</summary>
    public static RouteGroupBuilder MapIssueEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/projects/{projectId:long}/issues", ListIssuesAsync);
        group.MapGet("/issues/{issueId:long}", GetIssueAsync);
        group.MapPatch("/issues/{issueId:long}", UpdateIssueAsync);
        group.MapDelete("/issues/{issueId:long}", DeleteIssueAsync);
        group.MapPost("/issues/bulk", BulkUpdateIssuesAsync);

        return group;
    }

    private static async Task<IResult> ListIssuesAsync(
        long projectId,
        string? status,
        string? level,
        string? environment,
        string? release,
        string? label,
        string? q,
        string? sort,
        string? limit,
        string? cursor,
        ManagementIssueStore store,
        CancellationToken cancellationToken)
    {
        if (!ManagementValidation.TryNormalizeStatusFilter(status, out var statusFilter))
        {
            return ManagementHttp.BadRequest("status must be one of unresolved, resolved, ignored or all.");
        }

        if (!ManagementValidation.TryNormalizeLevelFilter(level, out var levelFilter))
        {
            return ManagementHttp.BadRequest("level must be one of debug, info, warning, error or fatal.");
        }

        if (!ManagementValidation.TryParseSort(sort, out var sortValue))
        {
            return ManagementHttp.BadRequest("sort must be one of last_seen, first_seen or count.");
        }

        if (!ManagementValidation.TryResolveLimit(limit, out var pageSize))
        {
            return ManagementHttp.BadRequest("limit must be between 1 and 100.");
        }

        IssueCursor? decodedCursor = null;
        if (cursor is not null)
        {
            if (!ManagementValidation.TryDecodeIssueCursor(cursor, sortValue, out var value))
            {
                return ManagementHttp.BadRequest("cursor is malformed.");
            }

            decodedCursor = value;
        }

        var query = new IssueListQuery(
            projectId,
            statusFilter,
            levelFilter,
            ManagementValidation.NormalizeFilter(environment),
            ManagementValidation.NormalizeFilter(release),
            ManagementValidation.NormalizeFilter(label),
            ManagementValidation.NormalizeFilter(q),
            sortValue,
            pageSize,
            decodedCursor);

        var page = await store.ListAsync(query, cancellationToken).ConfigureAwait(false);

        return ManagementHttp.Json(page, ProcessorJsonContext.Default.IssueListResponse);
    }

    private static async Task<IResult> GetIssueAsync(
        long issueId,
        ManagementIssueStore store,
        CancellationToken cancellationToken)
    {
        var issue = await store.GetAsync(issueId, cancellationToken).ConfigureAwait(false);

        return issue is null
            ? ManagementHttp.NotFound($"Issue {issueId} was not found.")
            : ManagementHttp.Json(issue, ProcessorJsonContext.Default.IssueDetailResponse);
    }

    private static async Task<IResult> UpdateIssueAsync(
        long issueId,
        HttpRequest request,
        ManagementIssueStore store,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.UpdateIssueRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return ManagementHttp.BadRequest("A JSON body with status and/or labels is required.");
        }

        string? status = null;
        if (body.Status is not null)
        {
            var normalized = body.Status.Trim().ToLowerInvariant();
            if (normalized is not ("unresolved" or "resolved" or "ignored"))
            {
                return ManagementHttp.BadRequest("status must be one of unresolved, resolved or ignored.");
            }

            status = normalized;
        }

        List<string>? labels = null;
        if (body.Labels is not null)
        {
            if (!ManagementValidation.TryNormalizeLabels(body.Labels, out var names, out var error))
            {
                return ManagementHttp.BadRequest(error!);
            }

            labels = names;
        }

        var issue = await store.UpdateAsync(issueId, status, labels, cancellationToken).ConfigureAwait(false);

        return issue is null
            ? ManagementHttp.NotFound($"Issue {issueId} was not found.")
            : ManagementHttp.Json(issue, ProcessorJsonContext.Default.IssueResponse);
    }

    private static async Task<IResult> DeleteIssueAsync(
        long issueId,
        ManagementIssueStore store,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteAsync(issueId, cancellationToken).ConfigureAwait(false);

        return deleted
            ? Results.NoContent()
            : ManagementHttp.NotFound($"Issue {issueId} was not found.");
    }

    private static async Task<IResult> BulkUpdateIssuesAsync(
        HttpRequest request,
        ManagementIssueStore store,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.BulkIssuesRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body?.Ids is null || body.Ids.Length == 0)
        {
            return ManagementHttp.BadRequest("ids must contain at least one issue id.");
        }

        if (body.Ids.Length > ManagementValidation.MaxBulkIds)
        {
            return ManagementHttp.BadRequest(
                $"ids must not contain more than {ManagementValidation.MaxBulkIds} issue ids.");
        }

        if (!ManagementValidation.TryParseBulkAction(body.Action, out var kind, out var error))
        {
            return ManagementHttp.BadRequest(error!);
        }

        List<string>? labels = null;
        if (kind is BulkIssueActionKind.AddLabels or BulkIssueActionKind.RemoveLabels)
        {
            if (body.Labels is null)
            {
                return ManagementHttp.BadRequest("labels is required for add_labels and remove_labels.");
            }

            if (!ManagementValidation.TryNormalizeLabels(body.Labels, out var names, out var labelError))
            {
                return ManagementHttp.BadRequest(labelError!);
            }

            if (names.Count == 0)
            {
                return ManagementHttp.BadRequest("labels must contain at least one name.");
            }

            labels = names;
        }

        var affected = await store
            .BulkAsync(body.Ids, new BulkIssueAction(kind, labels), cancellationToken)
            .ConfigureAwait(false);

        return ManagementHttp.Json(
            new BulkIssuesResponse(affected),
            ProcessorJsonContext.Default.BulkIssuesResponse);
    }
}
