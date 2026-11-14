using System.Text.Json;

namespace GlitchPort.Processor.Management;

/// <summary>
/// A label defined for a project. Labels are attached to issues through
/// <c>issue_labels</c>.
/// </summary>
internal sealed record LabelResponse(long Id, string Name, string Color);

/// <summary>
/// The issue representation shared by the list, update and bulk endpoints.
/// </summary>
internal sealed record IssueResponse(
    long Id,
    string Title,
    string? Culprit,
    string Level,
    string Platform,
    string Status,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    long EventCount,
    string? LastRelease,
    string? LastEnvironment,
    IReadOnlyList<LabelResponse> Labels);

/// <summary>One page of issues plus the cursor of the next page.</summary>
internal sealed record IssueListResponse(IReadOnlyList<IssueResponse> Items, string? NextCursor);

/// <summary>
/// A single issue including its labels and the most recent event, which
/// carries the full payload.
/// </summary>
internal sealed record IssueDetailResponse(
    long Id,
    string Title,
    string? Culprit,
    string Level,
    string Platform,
    string Status,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    long EventCount,
    string? LastRelease,
    string? LastEnvironment,
    IReadOnlyList<LabelResponse> Labels,
    EventDetailResponse? LatestEvent);

/// <summary>An event without its payload, as returned by the issue event list.</summary>
internal sealed record EventSummaryResponse(
    Guid Id,
    DateTimeOffset Timestamp,
    DateTimeOffset ReceivedAt,
    string Level,
    string? Environment,
    string? Release,
    string? UserId,
    string? Message);

/// <summary>One page of events plus the cursor of the next page.</summary>
internal sealed record EventListResponse(IReadOnlyList<EventSummaryResponse> Items, string? NextCursor);

/// <summary>An event together with the full JSON payload received from the SDK.</summary>
internal sealed record EventDetailResponse(
    Guid Id,
    long ProjectId,
    long IssueId,
    DateTimeOffset Timestamp,
    DateTimeOffset ReceivedAt,
    string Level,
    string Platform,
    string? Environment,
    string? Release,
    string? UserId,
    string Endpoint,
    string? Message,
    JsonElement Payload);

/// <summary>One bucket of the events-over-time statistics.</summary>
internal sealed record StatsBucketResponse(DateTimeOffset Bucket, long Count);

/// <summary>Request body of <c>PATCH /api/manage/issues/{id}</c>.</summary>
internal sealed record UpdateIssueRequest(string? Status, IReadOnlyList<string>? Labels);

/// <summary>Request body of <c>POST /api/manage/issues/bulk</c>.</summary>
internal sealed record BulkIssuesRequest(long[]? Ids, string? Action, IReadOnlyList<string>? Labels);

/// <summary>Response body of <c>POST /api/manage/issues/bulk</c>.</summary>
internal sealed record BulkIssuesResponse(long Affected);

/// <summary>Request body of <c>POST /api/manage/projects/{projectId}/labels</c>.</summary>
internal sealed record CreateLabelRequest(string? Name, string? Color);

/// <summary>Request body of <c>PATCH /api/manage/labels/{id}</c>.</summary>
internal sealed record UpdateLabelRequest(string? Name, string? Color);

/// <summary>Error body returned by the management API for 4xx responses.</summary>
internal sealed record ManagementErrorResponse(string Error);

/// <summary>Supported issue sort orders.</summary>
internal enum IssueSort
{
    /// <summary>Most recently seen issues first (the default).</summary>
    LastSeen,

    /// <summary>Issues whose first event is the most recent first.</summary>
    FirstSeen,

    /// <summary>Issues with the most events first.</summary>
    Count,
}

/// <summary>A decoded keyset cursor for the issue list.</summary>
/// <param name="SortValue">The sort value of the last row of the previous page.</param>
/// <param name="Id">The issue id of the last row of the previous page.</param>
internal readonly record struct IssueCursor(string SortValue, long Id);

/// <summary>A decoded keyset cursor for the issue event list.</summary>
internal readonly record struct EventCursor(DateTimeOffset Timestamp, Guid Id);

/// <summary>Everything the issue list query supports.</summary>
internal sealed record IssueListQuery(
    long ProjectId,
    string? Status,
    string? Level,
    string? Environment,
    string? Release,
    string? Label,
    string? Search,
    IssueSort Sort,
    int Limit,
    IssueCursor? Cursor);

/// <summary>Bulk actions accepted by <c>POST /api/manage/issues/bulk</c>.</summary>
internal enum BulkIssueActionKind
{
    /// <summary>Marks the issues as resolved.</summary>
    Resolve,

    /// <summary>Marks the issues as unresolved.</summary>
    Unresolve,

    /// <summary>Marks the issues as ignored.</summary>
    Ignore,

    /// <summary>Deletes the issues and their events.</summary>
    Delete,

    /// <summary>Adds the given label names to the issues.</summary>
    AddLabels,

    /// <summary>Removes the given label names from the issues.</summary>
    RemoveLabels,
}

/// <summary>A validated bulk action together with its label names.</summary>
internal sealed record BulkIssueAction(BulkIssueActionKind Kind, IReadOnlyList<string>? Labels);

/// <summary>Outcome of a label create or update.</summary>
internal enum LabelMutationStatus
{
    /// <summary>The label was written.</summary>
    Ok,

    /// <summary>No label with the given id exists.</summary>
    NotFound,

    /// <summary>Another label of the project already uses the name.</summary>
    Conflict,
}

/// <summary>The label (if any) produced by a create or update.</summary>
internal sealed record LabelMutationResult(LabelMutationStatus Status, LabelResponse? Label);
