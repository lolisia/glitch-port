using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GlitchPort.Processor.Retention;

/// <summary>
/// Deletes events and legacy messages that are older than the configured
/// retention window and removes issues that no longer have any events.
/// </summary>
/// <remarks>
/// <para>
/// A run is a single-node operation: it holds the session advisory lock
/// <see cref="AdvisoryLockKey"/> so two Processor instances never clean up at
/// the same time. When another session already holds the lock the run logs at
/// debug level and returns <see langword="null"/> without touching any data.
/// </para>
/// <para>
/// Expired events are deleted oldest first, at most <see cref="RetentionOptions.BatchSize"/>
/// rows per transaction. Right after every batch the affected issues get their
/// <c>event_count</c> recomputed from the events that are left; the issues are
/// updated in ascending id order so concurrent writers lock issue rows in a
/// stable order. Once no expired event remains, empty issues whose
/// <c>last_seen</c> is older than the cutoff and expired legacy messages are
/// removed. Every executed run writes exactly one <c>maintenance_runs</c> row.
/// </para>
/// </remarks>
public sealed class RetentionRunner
{
    /// <summary>
    /// Session advisory lock key that serializes retention runs. It must
    /// differ from <c>DatabaseMigrator.AdvisoryLockKey</c>.
    /// </summary>
    public const long AdvisoryLockKey = 823652;

    /// <summary>Value of <c>maintenance_runs.kind</c> written by this runner.</summary>
    public const string Kind = "retention";

    private static readonly string TryLockSql = $"select pg_try_advisory_lock({AdvisoryLockKey})";
    private static readonly string UnlockSql = $"select pg_advisory_unlock({AdvisoryLockKey})";

    // Oldest first, at most BatchSize rows per transaction. DELETE ... RETURNING
    // does not promise an order, so the issue ids are sorted again below.
    private const string DeleteEventsBatchSql = """
        delete from events
        where id in (
            select id
            from events
            where received_at < @Cutoff
            order by received_at, id
            limit @BatchSize
        )
        returning issue_id
        """;

    private const string RecomputeIssueCountSql = """
        update issues
        set event_count = (
            select count(*)
            from events
            where events.issue_id = issues.id
        )
        where id = @IssueId
        """;

    private const string DeleteEmptyIssuesSql = """
        delete from issues
        where event_count = 0
          and last_seen < @Cutoff
        """;

    private const string DeleteMessagesBatchSql = """
        delete from messages
        where id in (
            select id
            from messages
            where received_at < @Cutoff
            order by received_at, id
            limit @BatchSize
        )
        """;

    private const string InsertRunSql = """
        insert into maintenance_runs (
            kind,
            started_at,
            finished_at,
            days,
            deleted_events,
            deleted_issues,
            deleted_messages)
        values (
            @Kind,
            @StartedAt,
            @FinishedAt,
            @Days,
            @DeletedEvents,
            @DeletedIssues,
            @DeletedMessages)
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IOptions<RetentionOptions> _options;
    private readonly ILogger<RetentionRunner> _logger;

    /// <summary>
    /// Creates a runner that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public RetentionRunner(
        NpgsqlDataSource dataSource,
        IOptions<RetentionOptions> options,
        ILogger<RetentionRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Executes one retention run.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the run.</param>
    /// <returns>
    /// The counters of the executed run, or <see langword="null"/> when
    /// retention is disabled (<see cref="RetentionOptions.Days"/> is 0) or
    /// another session already holds the retention advisory lock.
    /// </returns>
    public async Task<RetentionRunResult?> RunAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var days = options.Days;
        var batchSize = options.BatchSize;

        if (days <= 0)
        {
            _logger.LogDebug("Retention is disabled because Days is {Days}.", days);
            return null;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var cutoff = startedAt.AddDays(-days);
        var stopwatch = Stopwatch.StartNew();

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await TryAcquireLockAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug(
                "Retention run skipped because another session holds the advisory lock {AdvisoryLockKey}.",
                AdvisoryLockKey);
            return null;
        }

        _logger.LogInformation(
            "Retention run started (Days {Days}, BatchSize {BatchSize}, DeletedEvents {DeletedEvents}, DeletedIssues {DeletedIssues}, DeletedMessages {DeletedMessages}, DurationMs {DurationMs}).",
            days,
            batchSize,
            0L,
            0L,
            0L,
            0L);

        try
        {
            var deletedEvents = await DeleteExpiredEventsAsync(connection, cutoff, batchSize, cancellationToken)
                .ConfigureAwait(false);

            var deletedIssues = await DeleteEmptyIssuesAsync(connection, cutoff, cancellationToken)
                .ConfigureAwait(false);

            var deletedMessages = await DeleteExpiredMessagesAsync(connection, cutoff, batchSize, cancellationToken)
                .ConfigureAwait(false);

            var finishedAt = DateTimeOffset.UtcNow;
            await InsertRunAsync(
                    connection,
                    startedAt,
                    finishedAt,
                    days,
                    deletedEvents,
                    deletedIssues,
                    deletedMessages,
                    cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();
            _logger.LogInformation(
                "Retention run finished (Days {Days}, BatchSize {BatchSize}, DeletedEvents {DeletedEvents}, DeletedIssues {DeletedIssues}, DeletedMessages {DeletedMessages}, DurationMs {DurationMs}).",
                days,
                batchSize,
                deletedEvents,
                deletedIssues,
                deletedMessages,
                stopwatch.ElapsedMilliseconds);

            return new RetentionRunResult(
                startedAt,
                finishedAt,
                days,
                deletedEvents,
                deletedIssues,
                deletedMessages);
        }
        finally
        {
            await ReleaseLockAsync(connection).ConfigureAwait(false);
        }
    }

    private static async Task<bool> TryAcquireLockAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken) =>
        await connection
            .ExecuteScalarAsync<bool>(new CommandDefinition(TryLockSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    /// <summary>
    /// Deletes expired events in batches of <paramref name="batchSize"/> and
    /// recomputes the counters of the issues that lost rows after every batch.
    /// </summary>
    private static async Task<long> DeleteExpiredEventsAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken)
    {
        long deleted = 0;

        while (true)
        {
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var issueIds = (await connection
                        .QueryAsync<long>(new CommandDefinition(
                            DeleteEventsBatchSql,
                            new { Cutoff = cutoff, BatchSize = batchSize },
                            transaction,
                            cancellationToken: cancellationToken))
                        .ConfigureAwait(false))
                    .ToArray();

                if (issueIds.Length == 0)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return deleted;
                }

                // Ascending id order keeps the issue row locks in a stable
                // order, just like the event writer's ordered updates.
                foreach (var issueId in issueIds.Distinct().Order())
                {
                    await connection
                        .ExecuteAsync(new CommandDefinition(
                            RecomputeIssueCountSql,
                            new { IssueId = issueId },
                            transaction,
                            cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                deleted += issueIds.Length;
            }
            catch
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task<long> DeleteEmptyIssuesAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken) =>
        await connection
            .ExecuteAsync(new CommandDefinition(
                DeleteEmptyIssuesSql,
                new { Cutoff = cutoff },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    /// <summary>
    /// Deletes expired legacy messages in batches of <paramref name="batchSize"/>,
    /// one transaction per batch.
    /// </summary>
    private static async Task<long> DeleteExpiredMessagesAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken)
    {
        long deleted = 0;

        while (true)
        {
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var rows = await connection
                    .ExecuteAsync(new CommandDefinition(
                        DeleteMessagesBatchSql,
                        new { Cutoff = cutoff, BatchSize = batchSize },
                        transaction,
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                deleted += rows;

                if (rows < batchSize)
                {
                    return deleted;
                }
            }
            catch
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task InsertRunAsync(
        NpgsqlConnection connection,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        int days,
        long deletedEvents,
        long deletedIssues,
        long deletedMessages,
        CancellationToken cancellationToken) =>
        await connection
            .ExecuteAsync(new CommandDefinition(
                InsertRunSql,
                new
                {
                    Kind,
                    StartedAt = startedAt,
                    FinishedAt = finishedAt,
                    Days = days,
                    DeletedEvents = deletedEvents,
                    DeletedIssues = deletedIssues,
                    DeletedMessages = deletedMessages,
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        try
        {
            // CancellationToken.None: the lock must be released even when the
            // caller's token has already fired. A closed connection releases
            // the session-scoped lock automatically.
            await connection
                .ExecuteAsync(new CommandDefinition(UnlockSql, cancellationToken: CancellationToken.None))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not release the retention advisory lock explicitly.");
        }
    }

    private static async Task RollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // The connection is already unusable; PostgreSQL rolls back on
            // disconnect regardless.
        }
    }
}
