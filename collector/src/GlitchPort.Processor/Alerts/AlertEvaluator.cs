using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GlitchPort.Processor.Alerts;

/// <summary>Counters of one event-frequency evaluation.</summary>
/// <param name="RulesEvaluated">Number of enabled event_frequency rules that were checked.</param>
/// <param name="Queued">Number of notifications the run inserted.</param>
/// <param name="Deleted">Number of expired sent or failed notifications the run removed.</param>
public sealed record AlertEvaluationResult(int RulesEvaluated, int Queued, long Deleted);

/// <summary>
/// Queues <c>event_frequency</c> notifications: for every enabled
/// event_frequency rule it counts the events of the configured window per
/// issue and queues one notification per issue that reaches the threshold.
/// </summary>
/// <remarks>
/// <para>
/// A run is a single-node operation like the retention job: it holds the
/// session advisory lock <see cref="AdvisoryLockKey"/> so two Processor
/// instances never evaluate at the same time. A run that cannot take the lock
/// returns <see langword="null"/> without touching any data.
/// </para>
/// <para>
/// The dedupe key contains the cooldown bucket
/// (<c>floor(epoch_seconds / (cooldown_minutes * 60))</c>), so an issue can be
/// notified at most once per cooldown window even when the evaluator runs
/// repeatedly or the same bucket is evaluated twice. The run also deletes sent
/// and failed notifications older than <see cref="AlertOptions.RetentionDays"/>
/// while it holds the lock.
/// </para>
/// </remarks>
public sealed class AlertEvaluator
{
    /// <summary>
    /// Session advisory lock key that serializes evaluations. It must differ
    /// from <c>DatabaseMigrator.AdvisoryLockKey</c> and
    /// <c>RetentionRunner.AdvisoryLockKey</c>.
    /// </summary>
    public const long AdvisoryLockKey = 823653;

    private static readonly string TryLockSql = $"select pg_try_advisory_lock({AdvisoryLockKey})";
    private static readonly string UnlockSql = $"select pg_advisory_unlock({AdvisoryLockKey})";

    private const string SelectFrequencyRulesSql = """
        select id as "Id",
               project_id as "ProjectId",
               name as "Name",
               trigger as "Trigger",
               min_level as "MinLevel",
               environment as "Environment",
               threshold as "Threshold",
               window_minutes as "WindowMinutes",
               cooldown_minutes as "CooldownMinutes",
               channel as "Channel",
               target_url as "TargetUrl",
               is_enabled as "IsEnabled"
        from alert_rules
        where is_enabled and trigger = 'event_frequency'
        order by id
        """;

    private const string CountMatchesSql = """
        select e.issue_id as "IssueId",
               count(*) as "EventCount"
        from events e
        where e.project_id = @ProjectId
          and e.received_at >= now() - (@WindowMinutes * interval '1 minute')
          and (cast(@MinLevel as text) is null
               or case lower(e.level)
                      when 'fatal' then 4
                      when 'error' then 3
                      when 'warning' then 2
                      when 'info' then 1
                      else 0
                  end >= @MinSeverity)
          and (cast(@Environment as text) is null or e.environment = @Environment)
        group by e.issue_id
        having count(*) >= @Threshold
        order by e.issue_id
        """;

    private const string DeleteExpiredSql = """
        delete from alert_notifications
        where status in ('sent', 'failed')
          and created_at < now() - (@Days * interval '1 day')
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IOptions<AlertOptions> _options;
    private readonly ILogger<AlertEvaluator> _logger;

    /// <summary>
    /// Creates an evaluator that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public AlertEvaluator(
        NpgsqlDataSource dataSource,
        IOptions<AlertOptions> options,
        ILogger<AlertEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Executes one evaluation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the run.</param>
    /// <returns>
    /// The counters of the executed run, or <see langword="null"/> when
    /// another session already holds the evaluation advisory lock.
    /// </returns>
    public async Task<AlertEvaluationResult?> EvaluateAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var now = DateTimeOffset.UtcNow;

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await TryAcquireLockAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug(
                "Alert evaluation skipped because another session holds the advisory lock {AdvisoryLockKey}.",
                AdvisoryLockKey);
            return null;
        }

        try
        {
            var deleted = await DeleteExpiredAsync(connection, options.RetentionDays, cancellationToken)
                .ConfigureAwait(false);

            var rules = (await connection
                    .QueryAsync<AlertRuleRecord>(new CommandDefinition(
                        SelectFrequencyRulesSql,
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false))
                .ToList();

            var queued = 0;

            foreach (var rule in rules)
            {
                queued += await EvaluateRuleAsync(connection, rule, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (queued > 0 || deleted > 0)
            {
                _logger.LogInformation(
                    "Alert evaluation finished (RulesEvaluated {RulesEvaluated}, Queued {Queued}, Deleted {Deleted}).",
                    rules.Count,
                    queued,
                    deleted);
            }

            return new AlertEvaluationResult(rules.Count, queued, deleted);
        }
        finally
        {
            await ReleaseLockAsync(connection).ConfigureAwait(false);
        }
    }

    private async Task<int> EvaluateRuleAsync(
        NpgsqlConnection connection,
        AlertRuleRecord rule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (rule.Threshold is not { } threshold || rule.WindowMinutes is not { } windowMinutes)
        {
            // The table check constraint makes this unreachable; the guard
            // keeps a malformed row from producing a notification.
            return 0;
        }

        var severity = rule.MinLevel is null ? 0 : AlertLevels.Severity(rule.MinLevel);

        var parameters = new DynamicParameters();
        parameters.Add("ProjectId", rule.ProjectId, DbType.Int64);
        parameters.Add("WindowMinutes", windowMinutes, DbType.Int32);
        parameters.Add("MinLevel", rule.MinLevel, DbType.String);
        parameters.Add("MinSeverity", severity, DbType.Int32);
        parameters.Add("Environment", rule.Environment, DbType.String);
        parameters.Add("Threshold", threshold, DbType.Int64);

        var matches = (await connection
                .QueryAsync<FrequencyMatch>(new CommandDefinition(
                    CountMatchesSql,
                    parameters,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();

        if (matches.Count == 0)
        {
            return 0;
        }

        var projectRow = await connection
            .QuerySingleOrDefaultAsync<AlertProjectRow>(new CommandDefinition(
                AlertSql.SelectProject,
                new { ProjectId = rule.ProjectId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (projectRow is null)
        {
            return 0;
        }

        var issueIds = matches.Select(static match => match.IssueId).ToArray();
        var issues = (await connection
                .QueryAsync<AlertIssueRow>(new CommandDefinition(
                    AlertSql.SelectIssueSnapshots,
                    new { IssueIds = issueIds },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToDictionary(static row => row.Id);

        var bucket = now.ToUnixTimeSeconds() / (rule.CooldownMinutes * 60L);
        var projectPayload = projectRow.ToAlertProject();
        var queued = 0;

        foreach (var match in matches)
        {
            if (!issues.TryGetValue(match.IssueId, out var issue))
            {
                continue;
            }

            var dedupeKey = $"frequency:{rule.Id}:{match.IssueId}:{bucket}";
            var payload = AlertPayloads.Create(
                AlertTriggers.EventFrequency,
                projectPayload,
                issue.ToAlertIssue(),
                new AlertRuleRef(rule.Id, rule.Name),
                _options.Value.DashboardBaseUrl);

            queued += await connection
                .ExecuteAsync(new CommandDefinition(
                    AlertSql.InsertNotification,
                    new
                    {
                        RuleId = rule.Id,
                        ProjectId = rule.ProjectId,
                        IssueId = match.IssueId,
                        Trigger = AlertTriggers.EventFrequency,
                        DedupeKey = dedupeKey,
                        Payload = AlertPayloads.Serialize(payload),
                    },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        return queued;
    }

    private async Task<long> DeleteExpiredAsync(
        NpgsqlConnection connection,
        int retentionDays,
        CancellationToken cancellationToken)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        return await connection
            .ExecuteAsync(new CommandDefinition(
                DeleteExpiredSql,
                new { Days = retentionDays },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task<bool> TryAcquireLockAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken) =>
        await connection
            .ExecuteScalarAsync<bool>(new CommandDefinition(TryLockSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        try
        {
            // Use CancellationToken.None: the lock must be released even when
            // the caller's token has already fired. A closed connection
            // releases the session-scoped lock automatically.
            await connection
                .ExecuteAsync(new CommandDefinition(UnlockSql, cancellationToken: CancellationToken.None))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not release the alert evaluation advisory lock explicitly.");
        }
    }

    private sealed class FrequencyMatch
    {
        public long IssueId { get; init; }

        public long EventCount { get; init; }
    }
}
