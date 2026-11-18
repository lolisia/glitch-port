namespace GlitchPort.Processor.Management;

/// <summary>
/// One release aggregated from the stored events of a project.
/// </summary>
/// <param name="Release">The release value carried by the events.</param>
/// <param name="FirstSeen">Timestamp of the earliest stored event of the release.</param>
/// <param name="LastSeen">Timestamp of the newest stored event of the release.</param>
/// <param name="EventCount">Number of stored events of the release.</param>
/// <param name="IssueCount">Number of distinct issues with an event of the release.</param>
/// <param name="NewIssueCount">Number of issues whose <c>first_release</c> is the release.</param>
/// <param name="UnresolvedIssueCount">
/// Number of distinct issues with an event of the release whose current status is
/// <c>unresolved</c>.
/// </param>
internal sealed record ReleaseResponse(
    string Release,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    long EventCount,
    long IssueCount,
    long NewIssueCount,
    long UnresolvedIssueCount);

/// <summary>One page of releases plus the cursor of the next page.</summary>
internal sealed record ReleaseListResponse(IReadOnlyList<ReleaseResponse> Items, string? NextCursor);

/// <summary>A decoded keyset cursor for the release list.</summary>
/// <param name="LastSeen">The <c>last_seen</c> of the last release of the previous page.</param>
/// <param name="Release">The release of the last row of the previous page.</param>
internal readonly record struct ReleaseCursor(DateTimeOffset LastSeen, string Release);
