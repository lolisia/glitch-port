using System.Text.Json;

namespace GlitchPort.Processor.Management;

/// <summary>
/// An alert rule as exposed by the management API.
/// </summary>
/// <param name="Id">Rule id.</param>
/// <param name="ProjectId">Project the rule belongs to.</param>
/// <param name="Name">Human readable rule name.</param>
/// <param name="Trigger">Trigger: <c>new_issue</c>, <c>regression</c> or <c>event_frequency</c>.</param>
/// <param name="MinLevel">Lowest issue level the rule matches, or <c>null</c> for any level.</param>
/// <param name="Environment">Environment the rule is limited to, or <c>null</c> for any environment.</param>
/// <param name="Threshold">Frequency threshold; only set for <c>event_frequency</c> rules.</param>
/// <param name="WindowMinutes">Frequency window; only set for <c>event_frequency</c> rules.</param>
/// <param name="CooldownMinutes">Minutes between two notifications of the same issue.</param>
/// <param name="Channel">Delivery channel: <c>webhook</c>, <c>discord</c> or <c>slack</c>.</param>
/// <param name="TargetUrl">Absolute http(s) URL the notification is posted to.</param>
/// <param name="IsEnabled">Whether the rule matches.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Time of the last change.</param>
internal sealed record AlertRuleResponse(
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
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Response body of <c>GET /api/manage/projects/{id}/alert-rules</c>.</summary>
internal sealed record AlertRuleListResponse(IReadOnlyList<AlertRuleResponse> Items);

/// <summary>Request body of <c>POST /api/manage/projects/{id}/alert-rules</c>.</summary>
internal sealed record CreateAlertRuleRequest(
    string? Name,
    string? Trigger,
    string? MinLevel,
    string? Environment,
    int? Threshold,
    int? WindowMinutes,
    int? CooldownMinutes,
    string? Channel,
    string? TargetUrl,
    bool? IsEnabled);

/// <summary>
/// Request body of <c>PATCH /api/manage/alert-rules/{id}</c>. The properties
/// are JSON elements so an omitted field can be told apart from an explicit
/// null (which clears a nullable column).
/// </summary>
internal sealed record UpdateAlertRuleRequest(
    JsonElement Name,
    JsonElement Trigger,
    JsonElement MinLevel,
    JsonElement Environment,
    JsonElement Threshold,
    JsonElement WindowMinutes,
    JsonElement CooldownMinutes,
    JsonElement Channel,
    JsonElement TargetUrl,
    JsonElement IsEnabled);

/// <summary>Response body of <c>POST /api/manage/alert-rules/{id}/test</c>.</summary>
/// <param name="Delivered">Whether the target answered with a 2xx status code.</param>
/// <param name="StatusCode">HTTP status code, or <c>null</c> when the request failed before a response.</param>
/// <param name="Error">Failure text, or <c>null</c> when the notification was delivered.</param>
internal sealed record AlertTestResponse(bool Delivered, int? StatusCode, string? Error);

/// <summary>One entry of <c>GET /api/manage/projects/{id}/alert-notifications</c>.</summary>
internal sealed record AlertNotificationResponse(
    long Id,
    long RuleId,
    string RuleName,
    long? IssueId,
    string Trigger,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);

/// <summary>Response body of <c>GET /api/manage/projects/{id}/alert-notifications</c>.</summary>
internal sealed record AlertNotificationListResponse(IReadOnlyList<AlertNotificationResponse> Items);
