using Dapper;
using GlitchPort.Processor.Retention;
using Npgsql;

namespace GlitchPort.Processor.Management;

/// <summary>One <c>maintenance_runs</c> row as read for the status endpoint.</summary>
internal sealed record RetentionRunRow(
    int Days,
    DateTimeOffset FinishedAt,
    long DeletedEvents,
    long DeletedIssues);

/// <summary>
/// Reads the newest retention run for the maintenance status endpoint.
/// </summary>
internal sealed class ManagementMaintenanceStore
{
    private const string LastRetentionRunSql = """
        select days as "Days",
               finished_at as "FinishedAt",
               deleted_events as "DeletedEvents",
               deleted_issues as "DeletedIssues"
        from maintenance_runs
        where kind = @Kind
        order by finished_at desc
        limit 1
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public ManagementMaintenanceStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Returns the newest executed retention run, or <see langword="null"/>
    /// when no retention run has finished yet.
    /// </summary>
    public async Task<RetentionRunRow?> GetLastRetentionRunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await connection
            .QuerySingleOrDefaultAsync<RetentionRunRowRecord>(new CommandDefinition(
                LastRetentionRunSql,
                new { Kind = RetentionRunner.Kind },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null
            ? null
            : new RetentionRunRow(row.Days, AsUtc(row.FinishedAt), row.DeletedEvents, row.DeletedIssues);
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class RetentionRunRowRecord
    {
        public int Days { get; init; }

        // Npgsql reads timestamptz as a UTC DateTime; Dapper cannot convert it
        // to DateTimeOffset, so the conversion happens after mapping.
        public DateTime FinishedAt { get; init; }

        public long DeletedEvents { get; init; }

        public long DeletedIssues { get; init; }
    }
}
