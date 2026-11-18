using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Counts events per time bucket and issues and events per platform for the
/// project statistics endpoint.
/// </summary>
internal sealed class ManagementStatsStore
{
    private const int HourlyBucketCount = 24;
    private const int DailyBucketCount = 14;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public ManagementStatsStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Returns event counts for <paramref name="period"/> (<c>24h</c> or
    /// <c>14d</c>) as ascending UTC buckets, including buckets without
    /// events.
    /// </summary>
    public async Task<List<StatsBucketResponse>> GetAsync(
        long projectId,
        string period,
        CancellationToken cancellationToken)
    {
        var (first, end, bucketCount, truncateUnit) = ResolveWindow(period);
        var step = truncateUnit == "hour" ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);

        // date_trunc runs on the UTC wall clock so the buckets are independent
        // of the server's time zone. extract(epoch from timestamp) interprets
        // the value as UTC, which gives a stable numeric bucket key.
        var sql = $"""
            select (extract(epoch from date_trunc('{truncateUnit}', timestamp at time zone 'utc')))::bigint as "Bucket",
                   count(*) as "Count"
            from events
            where project_id = @ProjectId
              and timestamp >= @From
              and timestamp < @To
            group by 1
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection
            .QueryAsync<StatsBucketRow>(new CommandDefinition(
                sql,
                new { ProjectId = projectId, From = first, To = end },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var counts = new Dictionary<long, long>();
        foreach (var row in rows)
        {
            counts[row.Bucket] = row.Count;
        }

        var buckets = new List<StatsBucketResponse>(bucketCount);
        for (var index = 0; index < bucketCount; index++)
        {
            var bucket = first + (step * index);
            buckets.Add(new StatsBucketResponse(bucket, counts.GetValueOrDefault(bucket.ToUnixTimeSeconds())));
        }

        return buckets;
    }

    /// <summary>
    /// Returns the number of issues and the total number of their events per
    /// platform for the whole project, ordered by event count descending and
    /// platform name ascending.
    /// </summary>
    public async Task<List<PlatformStatsResponse>> GetPlatformStatsAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select platform as "Platform",
                   count(*) as "IssueCount",
                   coalesce(sum(event_count), 0)::bigint as "EventCount"
            from issues
            where project_id = @ProjectId
            group by platform
            order by sum(event_count) desc, platform asc
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection
            .QueryAsync<PlatformStatsRow>(new CommandDefinition(
                sql,
                new { ProjectId = projectId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows
            .Select(static row => new PlatformStatsResponse(row.Platform, row.IssueCount, row.EventCount))
            .ToList();
    }

    private static (DateTimeOffset First, DateTimeOffset End, int BucketCount, string TruncateUnit) ResolveWindow(
        string period)
    {
        var now = DateTimeOffset.UtcNow;

        if (string.Equals(period, "14d", StringComparison.Ordinal))
        {
            var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
            var firstDay = today.AddDays(-(DailyBucketCount - 1));
            return (firstDay, today.AddDays(1), DailyBucketCount, "day");
        }

        var currentHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var firstHour = currentHour.AddHours(-(HourlyBucketCount - 1));
        return (firstHour, currentHour.AddHours(1), HourlyBucketCount, "hour");
    }

    private sealed class StatsBucketRow
    {
        public long Bucket { get; init; }

        public long Count { get; init; }
    }

    private sealed class PlatformStatsRow
    {
        public string Platform { get; init; } = string.Empty;

        public long IssueCount { get; init; }

        public long EventCount { get; init; }
    }
}
