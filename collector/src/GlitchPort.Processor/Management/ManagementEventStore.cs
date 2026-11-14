using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Reads, deletes and paginates stored events for the management API.
/// </summary>
internal sealed class ManagementEventStore
{
    private const string SelectDetailColumns = """
        e.id as "Id",
        e.project_id as "ProjectId",
        e.issue_id as "IssueId",
        e.timestamp as "Timestamp",
        e.received_at as "ReceivedAt",
        e.level as "Level",
        e.platform as "Platform",
        e.environment as "Environment",
        e.release as "Release",
        e.user_id as "UserId",
        e.endpoint as "Endpoint",
        e.message as "Message",
        e.payload::text as "PayloadJson"
        """;

    // The list never reads the payload, which can be up to the event size
    // limit, so it selects only the summary columns.
    private const string SelectSummaryColumns = """
        e.id as "Id",
        e.timestamp as "Timestamp",
        e.received_at as "ReceivedAt",
        e.level as "Level",
        e.environment as "Environment",
        e.release as "Release",
        e.user_id as "UserId",
        e.message as "Message"
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public ManagementEventStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Returns whether the issue exists, so the event list can answer
    /// <c>404</c> for unknown issues instead of an empty page.
    /// </summary>
    public async Task<bool> IssueExistsAsync(long issueId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection
            .ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from issues where id = @IssueId)",
                new { IssueId = issueId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns one page of events of an issue, newest first.
    /// </summary>
    public async Task<EventListResponse> ListAsync(
        long issueId,
        int limit,
        EventCursor? cursor,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("IssueId", issueId, DbType.Int64);
        parameters.Add("Take", limit + 1, DbType.Int32);

        var cursorCondition = string.Empty;
        if (cursor is { } value)
        {
            cursorCondition = """

                  and (e.timestamp < @CursorTimestamp
                       or (e.timestamp = @CursorTimestamp and e.id < @CursorId))
                """;
            parameters.Add("CursorTimestamp", value.Timestamp, DbType.DateTimeOffset);
            parameters.Add("CursorId", value.Id, DbType.Guid);
        }

        var sql = $"""
            select {SelectSummaryColumns}
            from events e
            where e.issue_id = @IssueId{cursorCondition}
            order by e.timestamp desc, e.id desc
            limit @Take
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = (await connection
                .QueryAsync<EventSummaryRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = ManagementCursor.Encode(
                AsUtc(last.Timestamp).ToString("O", CultureInfo.InvariantCulture),
                last.Id.ToString("D", CultureInfo.InvariantCulture));
        }

        var items = rows
            .Select(row => new EventSummaryResponse(
                row.Id,
                AsUtc(row.Timestamp),
                AsUtc(row.ReceivedAt),
                row.Level,
                row.Environment,
                row.Release,
                row.UserId,
                row.Message))
            .ToList();

        return new EventListResponse(items, nextCursor);
    }

    /// <summary>
    /// Returns the most recent event of an issue, including its payload.
    /// </summary>
    public async Task<EventDetailResponse?> GetLatestForIssueAsync(long issueId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<EventDetailRow>(new CommandDefinition(
                $"""
                 select {SelectDetailColumns}
                 from events e
                 where e.issue_id = @IssueId
                 order by e.timestamp desc, e.id desc
                 limit 1
                 """,
                new { IssueId = issueId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : ToResponse(row);
    }

    /// <summary>
    /// Returns one event including its payload.
    /// </summary>
    public async Task<EventDetailResponse?> GetAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<EventDetailRow>(new CommandDefinition(
                $"""
                 select {SelectDetailColumns}
                 from events e
                 where e.id = @EventId
                 """,
                new { EventId = eventId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : ToResponse(row);
    }

    /// <summary>
    /// Deletes an event and decrements the <c>event_count</c> of its issue
    /// without deleting the issue.
    /// </summary>
    /// <returns><see langword="false"/> when the event does not exist.</returns>
    public async Task<bool> DeleteAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var issueId = await connection
                .QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                    "delete from events where id = @EventId returning issue_id",
                    new { EventId = eventId },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (issueId is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await connection
                .ExecuteAsync(new CommandDefinition(
                    "update issues set event_count = greatest(event_count - 1, 0) where id = @IssueId",
                    new { IssueId = issueId.Value },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static EventDetailResponse ToResponse(EventDetailRow row) => new(
        row.Id,
        row.ProjectId,
        row.IssueId,
        AsUtc(row.Timestamp),
        AsUtc(row.ReceivedAt),
        row.Level,
        row.Platform,
        row.Environment,
        row.Release,
        row.UserId,
        row.Endpoint,
        row.Message,
        ParsePayload(row.PayloadJson));

    private static JsonElement ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }

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

    private sealed class EventSummaryRow
    {
        public Guid Id { get; init; }

        public DateTime Timestamp { get; init; }

        public DateTime ReceivedAt { get; init; }

        public string Level { get; init; } = string.Empty;

        public string? Environment { get; init; }

        public string? Release { get; init; }

        public string? UserId { get; init; }

        public string? Message { get; init; }
    }

    private sealed class EventDetailRow
    {
        public Guid Id { get; init; }

        public long ProjectId { get; init; }

        public long IssueId { get; init; }

        public DateTime Timestamp { get; init; }

        public DateTime ReceivedAt { get; init; }

        public string Level { get; init; } = string.Empty;

        public string Platform { get; init; } = string.Empty;

        public string? Environment { get; init; }

        public string? Release { get; init; }

        public string? UserId { get; init; }

        public string Endpoint { get; init; } = string.Empty;

        public string? Message { get; init; }

        public string PayloadJson { get; init; } = "{}";
    }
}
