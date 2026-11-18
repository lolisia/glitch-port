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
    string? FirstRelease,
    string? LastRelease,
    string? LastEnvironment,
    DateTimeOffset? ResolvedAt,
    string? ResolvedInRelease,
    DateTimeOffset? RegressedAt,
    int RegressionCount,
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
    string? FirstRelease,
    string? LastRelease,
    string? LastEnvironment,
    DateTimeOffset? ResolvedAt,
    string? ResolvedInRelease,
    DateTimeOffset? RegressedAt,
    int RegressionCount,
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

/// <summary>The issue and event totals of one platform in a project.</summary>
internal sealed record PlatformStatsResponse(string Platform, long IssueCount, long EventCount);

/// <summary>
/// Response body of <c>GET /api/manage/projects/{projectId}/stats</c>: the
/// event buckets of the requested period plus the all-time totals per
/// platform.
/// </summary>
internal sealed record ManagementStatsResponse(
    IReadOnlyList<StatsBucketResponse> Buckets,
    IReadOnlyList<PlatformStatsResponse> ByPlatform);

/// <summary>Request body of <c>PATCH /api/manage/issues/{id}</c>.</summary>
/// <remarks>
/// <c>resolve_in</c> is only accepted together with <c>status: resolved</c>;
/// it selects whether the issue is resolved now (<c>now</c>, the default) or
/// at the release of its most recent event (<c>next_release</c>).
/// </remarks>
internal sealed record UpdateIssueRequest(string? Status, IReadOnlyList<string>? Labels, string? ResolveIn);

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

/// <summary>
/// Response body of <c>GET /api/manage/maintenance/retention</c>: the
/// configured retention window and the counters of the newest retention run.
/// </summary>
/// <param name="Days">Configured retention in days. 0 means retention is disabled.</param>
/// <param name="LastRunAt">Finish time of the newest retention run, or null when none ran yet.</param>
/// <param name="LastDeletedEvents">Events deleted by the newest retention run.</param>
/// <param name="LastDeletedIssues">Issues deleted by the newest retention run.</param>
internal sealed record RetentionStatusResponse(
    int Days,
    DateTimeOffset? LastRunAt,
    long LastDeletedEvents,
    long LastDeletedIssues);

/// <summary>One entry of <c>GET /api/manage/projects</c>.</summary>
internal sealed record ProjectListItemResponse(
    long Id,
    string Name,
    string Slug,
    IReadOnlyList<string> Platforms,
    DateTimeOffset CreatedAt,
    long IssueCount,
    long KeyCount);

/// <summary>The project detail returned by <c>GET/PATCH /api/manage/projects/{id}</c>.</summary>
internal sealed record ProjectDetailResponse(
    long Id,
    string Name,
    string Slug,
    IReadOnlyList<string> Platforms,
    DateTimeOffset CreatedAt,
    long IssueCount,
    long KeyCount,
    string? Dsn);

/// <summary>
/// The project created by <c>POST /api/manage/projects</c>, including the DSN
/// of the key created alongside it.
/// </summary>
internal sealed record CreatedProjectResponse(
    long Id,
    string Name,
    string Slug,
    IReadOnlyList<string> Platforms,
    DateTimeOffset CreatedAt,
    string Dsn);

/// <summary>A project key as exposed by the key management endpoints.</summary>
internal sealed record ProjectKeyResponse(
    long Id,
    string PublicKey,
    string? Label,
    bool IsActive,
    int? RateLimitPerMinute,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    string Dsn);

/// <summary>Request body of <c>POST /api/manage/projects</c>.</summary>
/// <remarks>
/// <c>platform</c> is no longer a project setting, but the request still keeps
/// the member as a <see cref="JsonElement"/> so that sending it is rejected
/// instead of silently ignored.
/// </remarks>
internal sealed record CreateProjectRequest(string? Name, string? Slug, JsonElement Platform);

/// <summary>Request body of <c>PATCH /api/manage/projects/{id}</c>.</summary>
/// <remarks>
/// <c>platform</c> is no longer a project setting, but the request still keeps
/// the member as a <see cref="JsonElement"/> so that sending it is rejected
/// instead of silently ignored.
/// </remarks>
internal sealed record UpdateProjectRequest(string? Name, JsonElement Platform);

/// <summary>Request body of <c>POST /api/manage/projects/{id}/keys</c>.</summary>
internal sealed record CreateProjectKeyRequest(string? Label, int? RateLimitPerMinute);

/// <summary>
/// Request body of <c>PATCH /api/manage/keys/{keyId}</c>. The properties are
/// JSON elements so an omitted field can be told apart from an explicit null.
/// </summary>
internal sealed record UpdateProjectKeyRequest(
    JsonElement Label,
    JsonElement RateLimitPerMinute,
    JsonElement IsActive);

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
    string? FirstRelease,
    string? Label,
    string? Search,
    IReadOnlyList<string>? Platforms,
    bool Regressed,
    IssueSort Sort,
    int Limit,
    IssueCursor? Cursor);

/// <summary>Resolution modes accepted by <c>resolve_in</c>.</summary>
internal enum IssueResolveMode
{
    /// <summary>Resolves immediately, without pinning a release.</summary>
    Now,

    /// <summary>Resolves in the release of the issue's most recent event.</summary>
    NextRelease,
}

/// <summary>Bulk actions accepted by <c>POST /api/manage/issues/bulk</c>.</summary>
internal enum BulkIssueActionKind
{
    /// <summary>Marks the issues as resolved.</summary>
    Resolve,

    /// <summary>Resolves each issue in its own most recent release.</summary>
    ResolveNextRelease,

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
