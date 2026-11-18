using Dapper;
using GlitchPort.Processor.Data;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GlitchPort.Processor.Alerts;

/// <summary>An enabled alert rule as read by the queueing pipeline.</summary>
internal sealed record AlertRuleRecord(
    long Id,
    long ProjectId,
    string Name,
    string Trigger,
    string? MinLevel,
    string? Environment,
    int? Threshold,
    int? WindowMinutes,
    int CooldownMinutes,
    string Channel,
    string TargetUrl,
    bool IsEnabled);

/// <summary>The alert rule triggers stored in <c>alert_rules.trigger</c>.</summary>
internal static class AlertTriggers
{
    /// <summary>Fires when an issue is created.</summary>
    public const string NewIssue = "new_issue";

    /// <summary>Fires when a resolved issue is reopened.</summary>
    public const string Regression = "regression";

    /// <summary>Fires when an issue collects enough events inside a window.</summary>
    public const string EventFrequency = "event_frequency";
}

/// <summary>
/// Queues alert notifications for the issues an event batch created or
/// regressed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EnqueueAsync"/> is called by <see cref="EventStore"/> with the
/// store's own connection and transaction, so a notification is only visible
/// when the events, the issue update and the notification commit together. A
/// rolled-back or retried batch therefore leaves no notification behind.
/// </para>
/// <para>
/// The enabled rules of a project are read once per batch. Duplicate queueing
/// (the same issue created or regressed again) is suppressed by the unique
/// <c>dedupe_key</c> with <c>on conflict do nothing</c>. Queueing is not
/// affected by <see cref="AlertOptions.Enabled"/>: a disabled Processor keeps
/// building the backlog for the workers that run later.
/// </para>
/// </remarks>
public sealed class AlertEnqueuer
{
    private const string SelectRulesSql = """
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
        where project_id = @ProjectId and is_enabled
        order by id
        """;

    private readonly IOptions<AlertOptions> _options;
    private readonly ILogger<AlertEnqueuer> _logger;

    /// <summary>
    /// Creates an enqueuer that builds dashboard links from
    /// <paramref name="options"/>.
    /// </summary>
    public AlertEnqueuer(IOptions<AlertOptions> options, ILogger<AlertEnqueuer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Inserts one notification per matching enabled rule for every issue the
    /// batch created or regressed, inside the caller's transaction.
    /// </summary>
    /// <param name="connection">Connection that owns the event-store transaction.</param>
    /// <param name="transaction">Transaction that stores the batch.</param>
    /// <param name="changes">One entry per issue that gained events.</param>
    /// <param name="cancellationToken">Cancellation token for the batch.</param>
    /// <returns>The number of notifications that were actually inserted.</returns>
    internal async Task<int> EnqueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<IssueChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
        {
            return 0;
        }

        var inserted = 0;
        var dashboardBaseUrl = _options.Value.DashboardBaseUrl;

        foreach (var project in changes.GroupBy(static change => change.ProjectId))
        {
            var rules = await LoadRulesAsync(connection, transaction, project.Key, cancellationToken)
                .ConfigureAwait(false);

            if (rules.Count == 0)
            {
                continue;
            }

            var projectRow = await connection
                .QuerySingleOrDefaultAsync<AlertProjectRow>(new CommandDefinition(
                    AlertSql.SelectProject,
                    new { ProjectId = project.Key },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (projectRow is null)
            {
                continue;
            }

            var issues = await LoadIssuesAsync(
                    connection,
                    transaction,
                    project.Select(static change => change.IssueId).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);

            var projectPayload = projectRow.ToAlertProject();

            foreach (var change in project)
            {
                if (!issues.TryGetValue(change.IssueId, out var issue))
                {
                    continue;
                }

                foreach (var rule in rules)
                {
                    if (!Matches(rule, change, out var trigger, out var dedupeKey))
                    {
                        continue;
                    }

                    var payload = AlertPayloads.Create(
                        trigger,
                        projectPayload,
                        issue.ToAlertIssue(),
                        new AlertRuleRef(rule.Id, rule.Name),
                        dashboardBaseUrl);

                    inserted += await connection
                        .ExecuteAsync(new CommandDefinition(
                            AlertSql.InsertNotification,
                            new
                            {
                                RuleId = rule.Id,
                                ProjectId = project.Key,
                                IssueId = change.IssueId,
                                Trigger = trigger,
                                DedupeKey = dedupeKey,
                                Payload = AlertPayloads.Serialize(payload),
                            },
                            transaction,
                            cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                }
            }
        }

        if (inserted > 0)
        {
            _logger.LogDebug("Queued {Count} alert notification(s) for {Issues} changed issue(s).", inserted, changes.Count);
        }

        return inserted;
    }

    /// <summary>
    /// Decides whether a rule matches a change and builds its dedupe key.
    /// </summary>
    private static bool Matches(
        AlertRuleRecord rule,
        IssueChange change,
        out string trigger,
        out string dedupeKey)
    {
        trigger = string.Empty;
        dedupeKey = string.Empty;

        string? dedupe;
        if (rule.Trigger == AlertTriggers.NewIssue)
        {
            if (!change.Created)
            {
                return false;
            }

            trigger = AlertTriggers.NewIssue;
            dedupe = $"new_issue:{rule.Id}:{change.IssueId}";
        }
        else if (rule.Trigger == AlertTriggers.Regression)
        {
            if (!change.Regressed)
            {
                return false;
            }

            trigger = AlertTriggers.Regression;
            dedupe = $"regression:{rule.Id}:{change.IssueId}:{change.RegressionCount}";
        }
        else
        {
            // event_frequency rules are handled by the evaluator.
            return false;
        }

        if (rule.MinLevel is not null
            && AlertLevels.Severity(change.Level) < AlertLevels.Severity(rule.MinLevel))
        {
            return false;
        }

        if (rule.Environment is not null
            && !string.Equals(rule.Environment, change.Environment, StringComparison.Ordinal))
        {
            return false;
        }

        dedupeKey = dedupe;
        return true;
    }

    private static async Task<List<AlertRuleRecord>> LoadRulesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long projectId,
        CancellationToken cancellationToken) =>
        (await connection
                .QueryAsync<AlertRuleRecord>(new CommandDefinition(
                    SelectRulesSql,
                    new { ProjectId = projectId },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();

    private static async Task<Dictionary<long, AlertIssueRow>> LoadIssuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long[] issueIds,
        CancellationToken cancellationToken)
    {
        var rows = await connection
            .QueryAsync<AlertIssueRow>(new CommandDefinition(
                AlertSql.SelectIssueSnapshots,
                new { IssueIds = issueIds },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.ToDictionary(static row => row.Id);
    }
}

/// <summary>Level ordering shared by the alert rule filters.</summary>
internal static class AlertLevels
{
    /// <summary>The levels accepted by <c>alert_rules.min_level</c>, lowest first.</summary>
    public static readonly string[] Names = ["debug", "info", "warning", "error", "fatal"];

    /// <summary>Returns the severity rank of <paramref name="level"/> (0 for unknown values).</summary>
    public static int Severity(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "debug" => 0,
        "info" => 1,
        "warning" => 2,
        "error" => 3,
        "fatal" => 4,
        _ => 0,
    };

    /// <summary>Checks whether <paramref name="level"/> is a known level name.</summary>
    public static bool IsKnown(string? level) =>
        level is not null && Array.IndexOf(Names, level.Trim().ToLowerInvariant()) >= 0;
}
