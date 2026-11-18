using System.Data;
using System.Globalization;
using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Aggregates the stored events of a project into releases for the release
/// list endpoint.
/// </summary>
/// <remarks>
/// The aggregation reads the <c>events</c> table, so it only covers events
/// that are still stored. The retention worker deletes events older than the
/// configured window: a release whose events have all expired disappears from
/// the list, and the counters of a partially expired release cover only the
/// events inside the retention window.
/// </remarks>
internal sealed class ManagementReleaseStore
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public ManagementReleaseStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Returns one page of releases matching the filter, ordered by
    /// <c>last_seen</c> descending and release name ascending.
    /// </summary>
    public async Task<ReleaseListResponse> ListAsync(
        long projectId,
        string? environment,
        int limit,
        ReleaseCursor? cursor,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ProjectId", projectId, DbType.Int64);
        parameters.Add("Take", limit + 1, DbType.Int32);

        var environmentCondition = string.Empty;
        if (environment is not null)
        {
            environmentCondition = "\n      and e.environment = @Environment";
            parameters.Add("Environment", environment, DbType.String);
        }

        // The rows are grouped by release, so the keyset condition compares the
        // group's newest timestamp and the release name.
        var cursorCondition = string.Empty;
        if (cursor is { } value)
        {
            cursorCondition = """

                 having (max(e.timestamp) < @CursorLastSeen
                         or (max(e.timestamp) = @CursorLastSeen and e.release > @CursorRelease))
                """;
            parameters.Add("CursorLastSeen", value.LastSeen, DbType.DateTimeOffset);
            parameters.Add("CursorRelease", value.Release, DbType.String);
        }

        var sql = $"""
            select e.release as "Release",
                   min(e.timestamp) as "FirstSeen",
                   max(e.timestamp) as "LastSeen",
                   count(*) as "EventCount",
                   count(distinct e.issue_id) as "IssueCount",
                   (select count(*)
                    from issues new_issue
                    where new_issue.project_id = @ProjectId
                      and new_issue.first_release = e.release) as "NewIssueCount",
                   count(distinct e.issue_id) filter (where i.status = 'unresolved') as "UnresolvedIssueCount"
            from events e
            join issues i on i.id = e.issue_id
            where e.project_id = @ProjectId
              and e.release is not null{environmentCondition}
            group by e.release{cursorCondition}
            order by max(e.timestamp) desc, e.release asc
            limit @Take
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = (await connection
                .QueryAsync<ReleaseRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = ManagementCursor.Encode(
                AsUtc(last.LastSeen).ToString("O", CultureInfo.InvariantCulture),
                last.Release);
        }

        var items = rows
            .Select(static row => new ReleaseResponse(
                row.Release,
                AsUtc(row.FirstSeen),
                AsUtc(row.LastSeen),
                row.EventCount,
                row.IssueCount,
                row.NewIssueCount,
                row.UnresolvedIssueCount))
            .ToList();

        return new ReleaseListResponse(items, nextCursor);
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class ReleaseRow
    {
        public string Release { get; init; } = string.Empty;

        public DateTime FirstSeen { get; init; }

        public DateTime LastSeen { get; init; }

        public long EventCount { get; init; }

        public long IssueCount { get; init; }

        public long NewIssueCount { get; init; }

        public long UnresolvedIssueCount { get; init; }
    }
}
