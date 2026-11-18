using System.Data;
using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Management;

/// <summary>One value of a tag facet and the number of sampled events carrying it.</summary>
/// <param name="Value">The non-empty value.</param>
/// <param name="Count">Number of sampled events with this value.</param>
internal sealed record TagValueResponse(string Value, long Count);

/// <summary>
/// Distribution of one tag key over the sampled events of an issue.
/// </summary>
/// <param name="Key">Facet key, for example <c>release</c> or <c>tags.map</c>.</param>
/// <param name="Total">Number of sampled events with a non-empty value of the key.</param>
/// <param name="Distinct">Number of distinct non-empty values of the key.</param>
/// <param name="Top">Most frequent values, ordered by count descending then value ascending.</param>
internal sealed record TagFacetResponse(
    string Key,
    long Total,
    long Distinct,
    IReadOnlyList<TagValueResponse> Top);

/// <summary>
/// Response body of <c>GET /api/manage/issues/{issueId}/tags</c>: the tag
/// distribution of the newest sampled events of the issue.
/// </summary>
/// <param name="IssueId">The issue the distribution was computed for.</param>
/// <param name="SampleSize">Number of events that were sampled.</param>
/// <param name="UserCount">Number of distinct non-null user ids among the sampled events.</param>
/// <param name="Tags">Fixed facets followed by the most frequent payload tag keys.</param>
internal sealed record IssueTagsResponse(
    long IssueId,
    int SampleSize,
    long UserCount,
    IReadOnlyList<TagFacetResponse> Tags);

/// <summary>
/// Computes the tag distribution of the newest events of an issue.
/// </summary>
/// <remarks>
/// Only the newest <see cref="DefaultSampleLimit"/> events are read, so the
/// counters describe a sample of the issue rather than its complete history.
/// The payload is never materialized in the application: PostgreSQL expands
/// the fixed payload paths and the <c>tags</c> object and aggregates them.
/// </remarks>
internal sealed class ManagementTagStore
{
    /// <summary>Default number of newest events the distribution is based on.</summary>
    public const int DefaultSampleLimit = 10_000;

    /// <summary>Largest number of payload tag keys that get their own facet.</summary>
    public const int MaxTagKeys = 20;

    /// <summary>Facets that are always returned, in this order.</summary>
    private static readonly string[] FixedKeys =
    [
        "release",
        "environment",
        "platform",
        "os.name",
        "device.model",
        "gpu.name",
        "gpu.api",
        "runtime.version",
    ];

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _sampleLimit;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>
    /// and reads at most <paramref name="sampleLimit"/> events per request.
    /// </summary>
    public ManagementTagStore(NpgsqlDataSource dataSource, int sampleLimit = DefaultSampleLimit)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        if (sampleLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleLimit), sampleLimit, "The sample limit must be positive.");
        }

        _dataSource = dataSource;
        _sampleLimit = sampleLimit;
    }

    /// <summary>
    /// Returns the tag distribution of an issue, or <see langword="null"/>
    /// when the issue does not exist.
    /// </summary>
    /// <param name="issueId">The issue to inspect.</param>
    /// <param name="limit">Maximum number of values returned per facet.</param>
    /// <param name="cancellationToken">Cancellation token for the request.</param>
    public async Task<IssueTagsResponse?> GetAsync(
        long issueId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var exists = await connection
            .ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from issues where id = @IssueId)",
                new { IssueId = issueId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (!exists)
        {
            return null;
        }

        // A JSON object is required for jsonb_each_text; any other shape of
        // "tags" (or a missing one) simply contributes no tag values.
        const string sql = """
            with sample as (
                select e.release,
                       e.environment,
                       e.platform,
                       e.user_id,
                       e.payload
                from events e
                where e.issue_id = @IssueId
                order by e.timestamp desc, e.id desc
                limit @SampleLimit
            ),
            sample_stats as (
                select count(*)::bigint as sample_size,
                       count(distinct user_id)::bigint as user_count
                from sample
            ),
            tag_keys as (
                select t.key
                from sample s
                cross join lateral jsonb_each_text(
                    case
                        when jsonb_typeof(s.payload -> 'tags') = 'object' then s.payload -> 'tags'
                    end) as t(key, value)
                where t.value <> ''
                group by t.key
                order by count(*) desc, t.key asc
                limit @TagKeyLimit
            ),
            facet_keys as (
                select key
                from (values ('release'),
                             ('environment'),
                             ('platform'),
                             ('os.name'),
                             ('device.model'),
                             ('gpu.name'),
                             ('gpu.api'),
                             ('runtime.version')) as fixed(key)
                union all
                select 'tags.' || t.key
                from tag_keys t
            ),
            facet_values as (
                select 'release'::text as key, s.release as value from sample s
                union all select 'environment', s.environment from sample s
                union all select 'platform', s.platform from sample s
                union all select 'os.name', s.payload #>> '{contexts,os,name}' from sample s
                union all select 'device.model', s.payload #>> '{contexts,device,model}' from sample s
                union all select 'gpu.name', s.payload #>> '{contexts,gpu,name}' from sample s
                union all select 'gpu.api', s.payload #>> '{contexts,gpu,api}' from sample s
                union all select 'runtime.version', s.payload #>> '{contexts,runtime,version}' from sample s
                union all
                select 'tags.' || t.key, t.value
                from sample s
                cross join lateral jsonb_each_text(
                    case
                        when jsonb_typeof(s.payload -> 'tags') = 'object' then s.payload -> 'tags'
                    end) as t(key, value)
            ),
            aggregated as (
                select v.key,
                       v.value,
                       count(*) as count,
                       sum(count(*)) over (partition by v.key) as total,
                       count(*) over (partition by v.key) as distinct_count,
                       row_number() over (partition by v.key order by count(*) desc, v.value asc) as rank
                from facet_values v
                join facet_keys k on k.key = v.key
                where v.value is not null and v.value <> ''
                group by v.key, v.value
            )
            select k.key as "Key",
                   a.value as "Value",
                   coalesce(a.count, 0) as "Count",
                   coalesce(a.total, 0)::bigint as "Total",
                   coalesce(a.distinct_count, 0)::bigint as "DistinctCount",
                   s.sample_size as "SampleSize",
                   s.user_count as "UserCount"
            from facet_keys k
            cross join sample_stats s
            left join aggregated a on a.key = k.key and a.rank <= @Limit
            order by k.key, a.rank nulls last, a.value
            """;

        var parameters = new DynamicParameters();
        parameters.Add("IssueId", issueId, DbType.Int64);
        parameters.Add("SampleLimit", _sampleLimit, DbType.Int32);
        parameters.Add("TagKeyLimit", MaxTagKeys, DbType.Int32);
        parameters.Add("Limit", limit, DbType.Int32);

        var rows = (await connection
                .QueryAsync<TagFacetRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();

        if (rows.Count == 0)
        {
            // facet_keys always yields the fixed keys, so at least one row is
            // expected; keep a defensive empty response for an empty database.
            return new IssueTagsResponse(issueId, 0, 0, []);
        }

        return new IssueTagsResponse(
            issueId,
            (int)rows[0].SampleSize,
            rows[0].UserCount,
            BuildFacets(rows));
    }

    private static List<TagFacetResponse> BuildFacets(IReadOnlyList<TagFacetRow> rows)
    {
        var byKey = rows
            .GroupBy(static row => row.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);

        var facets = new List<TagFacetResponse>(byKey.Count);

        foreach (var key in FixedKeys)
        {
            facets.Add(BuildFacet(key, byKey.GetValueOrDefault(key)));
        }

        // The SQL query keeps the 20 most frequent payload tag keys; ordering
        // them by their total reproduces that frequency order.
        var tagKeys = byKey.Keys
            .Where(static key => Array.IndexOf(FixedKeys, key) < 0)
            .OrderByDescending(key => byKey[key][0].Total)
            .ThenBy(static key => key, StringComparer.Ordinal);

        foreach (var key in tagKeys)
        {
            facets.Add(BuildFacet(key, byKey[key]));
        }

        return facets;
    }

    private static TagFacetResponse BuildFacet(string key, List<TagFacetRow>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return new TagFacetResponse(key, 0, 0, []);
        }

        var top = rows
            .Where(static row => row.Value is not null)
            .OrderByDescending(static row => row.Count)
            .ThenBy(static row => row.Value, StringComparer.Ordinal)
            .Select(static row => new TagValueResponse(row.Value!, row.Count))
            .ToList();

        return new TagFacetResponse(key, rows[0].Total, rows[0].DistinctCount, top);
    }

    private sealed class TagFacetRow
    {
        public string Key { get; init; } = string.Empty;

        public string? Value { get; init; }

        public long Count { get; init; }

        public long Total { get; init; }

        public long DistinctCount { get; init; }

        public long SampleSize { get; init; }

        public long UserCount { get; init; }
    }
}
