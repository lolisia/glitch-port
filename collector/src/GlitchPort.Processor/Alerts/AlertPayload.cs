using System.Text.Json;

namespace GlitchPort.Processor.Alerts;

/// <summary>
/// The common notification payload stored in <c>alert_notifications.payload</c>.
/// </summary>
/// <remarks>
/// The payload is written once when the notification is queued and is the
/// source of every channel format, so changing a rule after queueing does not
/// rewrite notifications that are already on their way.
/// </remarks>
/// <param name="Trigger">Trigger that produced the notification.</param>
/// <param name="Project">Project that owns the issue.</param>
/// <param name="Issue">Snapshot of the issue at queueing time.</param>
/// <param name="Rule">Rule that matched.</param>
/// <param name="Url">Dashboard link, or <c>null</c> when no base URL is configured.</param>
public sealed record AlertPayload(
    string Trigger,
    AlertProject Project,
    AlertIssue Issue,
    AlertRuleRef Rule,
    string? Url);

/// <summary>The project part of an <see cref="AlertPayload"/>.</summary>
public sealed record AlertProject(long Id, string Name, string Slug);

/// <summary>The issue part of an <see cref="AlertPayload"/>.</summary>
public sealed record AlertIssue(
    long Id,
    string Title,
    string? Culprit,
    string Level,
    string Status,
    long EventCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? FirstRelease,
    string? LastRelease,
    string? Environment);

/// <summary>The rule part of an <see cref="AlertPayload"/>.</summary>
public sealed record AlertRuleRef(long Id, string Name);

/// <summary>Builds dashboard links for notifications.</summary>
public static class AlertLinks
{
    /// <summary>
    /// Returns <c>{dashboardBaseUrl}#/issues/{issueId}</c>, or <c>null</c> when
    /// <paramref name="dashboardBaseUrl"/> is blank.
    /// </summary>
    public static string? Build(string? dashboardBaseUrl, long issueId)
    {
        if (string.IsNullOrWhiteSpace(dashboardBaseUrl))
        {
            return null;
        }

        return $"{dashboardBaseUrl.TrimEnd('/')}#/issues/{issueId}";
    }
}

/// <summary>
/// Creates the notification payloads shared by the event store enqueuer, the
/// frequency evaluator and the test endpoint.
/// </summary>
public static class AlertPayloads
{
    /// <summary>Creates the payload of a notification.</summary>
    public static AlertPayload Create(
        string trigger,
        AlertProject project,
        AlertIssue issue,
        AlertRuleRef rule,
        string? dashboardBaseUrl) =>
        new(trigger, project, issue, rule, AlertLinks.Build(dashboardBaseUrl, issue.Id));

    /// <summary>
    /// Serializes a payload with the Processor JSON context (snake_case keys).
    /// </summary>
    public static string Serialize(AlertPayload payload) =>
        JsonSerializer.Serialize(payload, ProcessorJsonContext.Default.AlertPayload);

    /// <summary>
    /// Deserializes a payload read from the database.
    /// </summary>
    /// <returns><c>null</c> when the stored JSON is not a valid payload.</returns>
    public static AlertPayload? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ProcessorJsonContext.Default.AlertPayload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Statements shared by the queueing pipeline.</summary>
internal static class AlertSql
{
    /// <summary>
    /// Post-update issue snapshot used to build payloads. The caller reads it
    /// inside the same transaction, so it carries the values the batch wrote.
    /// </summary>
    public const string SelectIssueSnapshots = """
        select id as "Id",
               title as "Title",
               culprit as "Culprit",
               level as "Level",
               status as "Status",
               event_count as "EventCount",
               first_seen as "FirstSeen",
               last_seen as "LastSeen",
               first_release as "FirstRelease",
               last_release as "LastRelease",
               last_environment as "Environment"
        from issues
        where id = any(@IssueIds)
        """;

    /// <summary>Project name and slug needed by every payload.</summary>
    public const string SelectProject = """
        select id as "Id",
               name as "Name",
               slug as "Slug"
        from projects
        where id = @ProjectId
        """;

    /// <summary>
    /// Inserts a notification unless its dedupe key was already queued. The
    /// unique constraint on <c>dedupe_key</c> is the conflict target.
    /// </summary>
    public const string InsertNotification = """
        insert into alert_notifications (rule_id, project_id, issue_id, trigger, dedupe_key, payload)
        values (@RuleId, @ProjectId, @IssueId, @Trigger, @DedupeKey, cast(@Payload as jsonb))
        on conflict (dedupe_key) do nothing
        """;
}

/// <summary>An issue row mapped from <see cref="AlertSql.SelectIssueSnapshots"/>.</summary>
internal sealed class AlertIssueRow
{
    /// <summary>Issue id.</summary>
    public long Id { get; init; }

    /// <summary>Issue title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Issue culprit, if any.</summary>
    public string? Culprit { get; init; }

    /// <summary>Issue level.</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>Issue status.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Number of stored events.</summary>
    public long EventCount { get; init; }

    /// <summary>First event timestamp.</summary>
    public DateTime FirstSeen { get; init; }

    /// <summary>Most recent event timestamp.</summary>
    public DateTime LastSeen { get; init; }

    /// <summary>Release of the earliest event, if any.</summary>
    public string? FirstRelease { get; init; }

    /// <summary>Release of the most recent event, if any.</summary>
    public string? LastRelease { get; init; }

    /// <summary>Environment of the most recent event, if any.</summary>
    public string? Environment { get; init; }

    /// <summary>Projects the row into the payload shape.</summary>
    public AlertIssue ToAlertIssue() =>
        new(
            Id,
            Title,
            Culprit,
            Level,
            Status,
            EventCount,
            AlertTimestamp.AsUtc(FirstSeen),
            AlertTimestamp.AsUtc(LastSeen),
            FirstRelease,
            LastRelease,
            Environment);
}

/// <summary>A project row mapped from <see cref="AlertSql.SelectProject"/>.</summary>
internal sealed class AlertProjectRow
{
    /// <summary>Project id.</summary>
    public long Id { get; init; }

    /// <summary>Project name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Project slug.</summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>Projects the row into the payload shape.</summary>
    public AlertProject ToAlertProject() => new(Id, Name, Slug);
}

/// <summary>Converts Npgsql <c>timestamptz</c> values read as UTC <see cref="DateTime"/>.</summary>
internal static class AlertTimestamp
{
    /// <summary>Reinterprets a UTC <see cref="DateTime"/> as a <see cref="DateTimeOffset"/>.</summary>
    public static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
