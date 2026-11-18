using System.Data;
using System.Globalization;
using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Reads and mutates issues, their labels and their bulk actions for the
/// management API.
/// </summary>
internal sealed class ManagementIssueStore
{
    private const string SelectIssueColumns = """
        i.id as "Id",
        i.title as "Title",
        i.culprit as "Culprit",
        i.level as "Level",
        i.platform as "Platform",
        i.status as "Status",
        i.first_seen as "FirstSeen",
        i.last_seen as "LastSeen",
        i.event_count as "EventCount",
        i.last_release as "LastRelease",
        i.last_environment as "LastEnvironment"
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ManagementEventStore _events;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public ManagementIssueStore(NpgsqlDataSource dataSource, ManagementEventStore events)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(events);

        _dataSource = dataSource;
        _events = events;
    }

    /// <summary>
    /// Returns one page of issues matching <paramref name="query"/>, newest
    /// first according to the requested sort.
    /// </summary>
    public async Task<IssueListResponse> ListAsync(IssueListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new DynamicParameters();
        parameters.Add("ProjectId", query.ProjectId, DbType.Int64);
        parameters.Add("Take", query.Limit + 1, DbType.Int32);

        var conditions = new List<string> { "i.project_id = @ProjectId" };

        if (query.Status is { } status)
        {
            conditions.Add("i.status = @Status");
            parameters.Add("Status", status, DbType.String);
        }

        if (query.Level is { } level)
        {
            conditions.Add("i.level = @Level");
            parameters.Add("Level", level, DbType.String);
        }

        if (query.Platforms is { Count: > 0 } platforms)
        {
            conditions.Add("i.platform = any(@Platforms::text[])");
            parameters.Add("Platforms", platforms.ToArray());
        }

        // Environment and release describe events, so an issue matches when
        // at least one of its events carries the value.
        if (query.Environment is { } environment)
        {
            conditions.Add("""
                exists (select 1
                        from events e
                        where e.issue_id = i.id and e.environment = @Environment)
                """);
            parameters.Add("Environment", environment, DbType.String);
        }

        if (query.Release is { } release)
        {
            conditions.Add("""
                exists (select 1
                        from events e
                        where e.issue_id = i.id and e.release = @Release)
                """);
            parameters.Add("Release", release, DbType.String);
        }

        if (query.Label is { } label)
        {
            conditions.Add("""
                exists (select 1
                        from issue_labels il
                        join labels l on l.id = il.label_id
                        where il.issue_id = i.id and l.name = @Label)
                """);
            parameters.Add("Label", label, DbType.String);
        }

        if (query.Search is { } search)
        {
            // Percent, underscore and backslash are escaped so the user's
            // text is matched literally instead of acting as wildcards.
            conditions.Add("""i.title ilike @TitlePattern escape '\'""");
            parameters.Add("TitlePattern", $"%{EscapeLikePattern(search)}%", DbType.String);
        }

        if (query.Cursor is { } cursor)
        {
            switch (query.Sort)
            {
                case IssueSort.Count:
                    conditions.Add("""
                        (i.event_count < @CursorSort
                         or (i.event_count = @CursorSort and i.id < @CursorId))
                        """);
                    parameters.Add(
                        "CursorSort",
                        long.Parse(cursor.SortValue, CultureInfo.InvariantCulture),
                        DbType.Int64);
                    break;

                case IssueSort.FirstSeen:
                    conditions.Add("""
                        (i.first_seen < @CursorSort
                         or (i.first_seen = @CursorSort and i.id < @CursorId))
                        """);
                    parameters.Add("CursorSort", ParseTimestamp(cursor.SortValue), DbType.DateTimeOffset);
                    break;

                default:
                    conditions.Add("""
                        (i.last_seen < @CursorSort
                         or (i.last_seen = @CursorSort and i.id < @CursorId))
                        """);
                    parameters.Add("CursorSort", ParseTimestamp(cursor.SortValue), DbType.DateTimeOffset);
                    break;
            }

            parameters.Add("CursorId", cursor.Id, DbType.Int64);
        }

        var orderBy = query.Sort switch
        {
            IssueSort.FirstSeen => "i.first_seen desc, i.id desc",
            IssueSort.Count => "i.event_count desc, i.id desc",
            _ => "i.last_seen desc, i.id desc",
        };

        var sql = $"""
            select {SelectIssueColumns}
            from issues i
            where {string.Join("\n  and ", conditions)}
            order by {orderBy}
            limit @Take
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = (await connection
                .QueryAsync<IssueRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();

        string? nextCursor = null;
        if (rows.Count > query.Limit)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = EncodeCursor(query.Sort, rows[^1]);
        }

        var labels = await ReadLabelsAsync(
                connection,
                transaction: null,
                rows.Select(static row => row.Id).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(row => ToResponse(row, LabelsFor(labels, row.Id)))
            .ToList();

        return new IssueListResponse(items, nextCursor);
    }

    /// <summary>
    /// Returns an issue with its labels and its latest event.
    /// </summary>
    public async Task<IssueDetailResponse?> GetAsync(long issueId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var row = await ReadIssueAsync(connection, transaction: null, issueId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var labels = await ReadLabelsAsync(connection, transaction: null, [issueId], cancellationToken)
            .ConfigureAwait(false);
        var latestEvent = await _events.GetLatestForIssueAsync(issueId, cancellationToken).ConfigureAwait(false);

        return new IssueDetailResponse(
            row.Id,
            row.Title,
            row.Culprit,
            row.Level,
            row.Platform,
            row.Status,
            AsUtc(row.FirstSeen),
            AsUtc(row.LastSeen),
            row.EventCount,
            row.LastRelease,
            row.LastEnvironment,
            LabelsFor(labels, issueId),
            latestEvent);
    }

    /// <summary>
    /// Applies a status change and/or replaces the issue's label set.
    /// </summary>
    /// <remarks>
    /// Labels that do not exist yet are created for the issue's project. The
    /// whole update runs in one transaction.
    /// </remarks>
    /// <returns>The updated issue, or <see langword="null"/> when it does not exist.</returns>
    public async Task<IssueResponse?> UpdateAsync(
        long issueId,
        string? status,
        IReadOnlyList<string>? labels,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var projectId = await connection
                .QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                    "select project_id from issues where id = @IssueId for update",
                    new { IssueId = issueId },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (projectId is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (status is not null)
            {
                await connection
                    .ExecuteAsync(new CommandDefinition(
                        "update issues set status = @Status where id = @IssueId",
                        new { Status = status, IssueId = issueId },
                        transaction,
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }

            if (labels is not null)
            {
                await ReplaceLabelsAsync(connection, transaction, issueId, projectId.Value, labels, cancellationToken)
                    .ConfigureAwait(false);
            }

            var row = await ReadIssueAsync(connection, transaction, issueId, cancellationToken).ConfigureAwait(false);
            var issueLabels = await ReadLabelsAsync(connection, transaction, [issueId], cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return row is null ? null : ToResponse(row, LabelsFor(issueLabels, issueId));
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Deletes an issue. Its events and label assignments are removed by the
    /// foreign keys.
    /// </summary>
    public async Task<bool> DeleteAsync(long issueId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection
            .ExecuteAsync(new CommandDefinition(
                "delete from issues where id = @IssueId",
                new { IssueId = issueId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>
    /// Applies a bulk action to the given issue ids.
    /// </summary>
    /// <returns>
    /// The number of issues the action modified: the issues matched by a
    /// status update, the issues deleted, the existing issues that received
    /// the labels, or the issues from which at least one label was removed.
    /// </returns>
    public async Task<long> BulkAsync(
        IReadOnlyList<long> ids,
        BulkIssueAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(action);

        var idArray = ids.Distinct().ToArray();
        if (idArray.Length == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        switch (action.Kind)
        {
            case BulkIssueActionKind.Resolve:
                return await UpdateStatusAsync(connection, idArray, "resolved", cancellationToken).ConfigureAwait(false);

            case BulkIssueActionKind.Unresolve:
                return await UpdateStatusAsync(connection, idArray, "unresolved", cancellationToken)
                    .ConfigureAwait(false);

            case BulkIssueActionKind.Ignore:
                return await UpdateStatusAsync(connection, idArray, "ignored", cancellationToken).ConfigureAwait(false);

            case BulkIssueActionKind.Delete:
                return await connection
                    .ExecuteAsync(new CommandDefinition(
                        "delete from issues where id = any(@Ids::bigint[])",
                        new { Ids = idArray },
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);

            case BulkIssueActionKind.AddLabels:
                return await AddLabelsAsync(connection, idArray, action.Labels ?? [], cancellationToken)
                    .ConfigureAwait(false);

            case BulkIssueActionKind.RemoveLabels:
                return await RemoveLabelsAsync(connection, idArray, action.Labels ?? [], cancellationToken)
                    .ConfigureAwait(false);

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "Unknown bulk action.");
        }
    }

    private static async Task<long> UpdateStatusAsync(
        NpgsqlConnection connection,
        long[] ids,
        string status,
        CancellationToken cancellationToken) =>
        await connection
            .ExecuteAsync(new CommandDefinition(
                "update issues set status = @Status where id = any(@Ids::bigint[])",
                new { Status = status, Ids = ids },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private static async Task<long> AddLabelsAsync(
        NpgsqlConnection connection,
        long[] ids,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return 0;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var nameArray = names.ToArray();

            // Bulk actions may span projects, so a missing label is created
            // once per project that owns one of the selected issues.
            await connection
                .ExecuteAsync(new CommandDefinition(
                    """
                    insert into labels (project_id, name)
                    select distinct i.project_id, n.name
                    from issues i
                    cross join unnest(@Names::text[]) as n(name)
                    where i.id = any(@Ids::bigint[])
                    on conflict (project_id, name) do nothing
                    """,
                    new { Names = nameArray, Ids = ids },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await connection
                .ExecuteAsync(new CommandDefinition(
                    """
                    insert into issue_labels (issue_id, label_id)
                    select i.id, l.id
                    from issues i
                    join labels l on l.project_id = i.project_id and l.name = any(@Names::text[])
                    where i.id = any(@Ids::bigint[])
                    on conflict (issue_id, label_id) do nothing
                    """,
                    new { Names = nameArray, Ids = ids },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            var affected = await connection
                .ExecuteScalarAsync<long>(new CommandDefinition(
                    "select count(*) from issues where id = any(@Ids::bigint[])",
                    new { Ids = ids },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long> RemoveLabelsAsync(
        NpgsqlConnection connection,
        long[] ids,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return 0;
        }

        var issueIds = await connection
            .QueryAsync<long>(new CommandDefinition(
                """
                delete from issue_labels il
                using labels l
                where il.label_id = l.id
                  and il.issue_id = any(@Ids::bigint[])
                  and l.name = any(@Names::text[])
                returning il.issue_id
                """,
                new { Names = names.ToArray(), Ids = ids },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return issueIds.Distinct().LongCount();
    }

    private static async Task ReplaceLabelsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long issueId,
        long projectId,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        await connection
            .ExecuteAsync(new CommandDefinition(
                "delete from issue_labels where issue_id = @IssueId",
                new { IssueId = issueId },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (labels.Count == 0)
        {
            return;
        }

        var names = labels.ToArray();

        await connection
            .ExecuteAsync(new CommandDefinition(
                """
                insert into labels (project_id, name)
                select @ProjectId::bigint, name
                from unnest(@Names::text[]) as name
                on conflict (project_id, name) do nothing
                """,
                new { ProjectId = projectId, Names = names },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await connection
            .ExecuteAsync(new CommandDefinition(
                """
                insert into issue_labels (issue_id, label_id)
                select @IssueId::bigint, l.id
                from labels l
                where l.project_id = @ProjectId and l.name = any(@Names::text[])
                on conflict (issue_id, label_id) do nothing
                """,
                new { IssueId = issueId, ProjectId = projectId, Names = names },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task<IssueRow?> ReadIssueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long issueId,
        CancellationToken cancellationToken) =>
        await connection
            .QuerySingleOrDefaultAsync<IssueRow>(new CommandDefinition(
                $"select {SelectIssueColumns} from issues i where i.id = @IssueId",
                new { IssueId = issueId },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private static async Task<Dictionary<long, List<LabelResponse>>> ReadLabelsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<long> issueIds,
        CancellationToken cancellationToken)
    {
        var byIssue = new Dictionary<long, List<LabelResponse>>();
        if (issueIds.Count == 0)
        {
            return byIssue;
        }

        var rows = await connection
            .QueryAsync<IssueLabelRow>(new CommandDefinition(
                """
                select il.issue_id as "IssueId",
                       l.id as "Id",
                       l.name as "Name",
                       l.color as "Color"
                from issue_labels il
                join labels l on l.id = il.label_id
                where il.issue_id = any(@IssueIds::bigint[])
                order by l.name, l.id
                """,
                new { IssueIds = issueIds.ToArray() },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (!byIssue.TryGetValue(row.IssueId, out var labels))
            {
                labels = [];
                byIssue[row.IssueId] = labels;
            }

            labels.Add(new LabelResponse(row.Id, row.Name, row.Color));
        }

        return byIssue;
    }

    private static IReadOnlyList<LabelResponse> LabelsFor(
        Dictionary<long, List<LabelResponse>> labels,
        long issueId) =>
        labels.TryGetValue(issueId, out var list) ? list : [];

    private static string EncodeCursor(IssueSort sort, IssueRow row) => sort switch
    {
        IssueSort.Count => ManagementCursor.Encode(
            row.EventCount.ToString(CultureInfo.InvariantCulture),
            row.Id.ToString(CultureInfo.InvariantCulture)),
        IssueSort.FirstSeen => ManagementCursor.Encode(
            AsUtc(row.FirstSeen).ToString("O", CultureInfo.InvariantCulture),
            row.Id.ToString(CultureInfo.InvariantCulture)),
        _ => ManagementCursor.Encode(
            AsUtc(row.LastSeen).ToString("O", CultureInfo.InvariantCulture),
            row.Id.ToString(CultureInfo.InvariantCulture)),
    };

    private static IssueResponse ToResponse(IssueRow row, IReadOnlyList<LabelResponse> labels) => new(
        row.Id,
        row.Title,
        row.Culprit,
        row.Level,
        row.Platform,
        row.Status,
        AsUtc(row.FirstSeen),
        AsUtc(row.LastSeen),
        row.EventCount,
        row.LastRelease,
        row.LastEnvironment,
        labels);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static async Task RollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // The connection is already unusable; PostgreSQL rolls back on
            // disconnect anyway.
        }
    }

    private sealed class IssueRow
    {
        public long Id { get; init; }

        public string Title { get; init; } = string.Empty;

        public string? Culprit { get; init; }

        public string Level { get; init; } = string.Empty;

        public string Platform { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public DateTime FirstSeen { get; init; }

        public DateTime LastSeen { get; init; }

        public long EventCount { get; init; }

        public string? LastRelease { get; init; }

        public string? LastEnvironment { get; init; }
    }

    private sealed class IssueLabelRow
    {
        public long IssueId { get; init; }

        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Color { get; init; } = string.Empty;
    }
}
