using System.Globalization;
using System.Text.Json;
using GlitchPort.Processor.Alerts;
using GlitchPort.Processor.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Alert rule management, the delivery test endpoint and the notification
/// listing.
/// </summary>
/// <remarks>
/// Rules are validated before they reach PostgreSQL: unknown triggers,
/// channels and levels are rejected, <c>event_frequency</c> rules require a
/// threshold and a window (and every other trigger rejects them), and the
/// target must be an absolute http(s) URL of at most 2048 characters. The
/// table check constraint enforces the trigger/threshold pairing once more.
/// </remarks>
internal static class ManagementAlertEndpoints
{
    private const int DefaultCooldownMinutes = 60;
    private const int MaxNameLength = 100;
    private const int MaxTargetUrlLength = 2048;
    private const int DefaultNotificationLimit = 50;
    private const int MaxNotificationLimit = 100;

    /// <summary>Trigger value the management API accepts for a test delivery.</summary>
    private const string TestTrigger = "test";

    private static readonly string[] Triggers =
        [AlertTriggers.NewIssue, AlertTriggers.Regression, AlertTriggers.EventFrequency];

    /// <summary>Maps the alert endpoints on <paramref name="group"/>.</summary>
    public static RouteGroupBuilder MapAlertEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/projects/{projectId:long}/alert-rules", ListRulesAsync);
        group.MapPost("/projects/{projectId:long}/alert-rules", CreateRuleAsync);
        group.MapPatch("/alert-rules/{ruleId:long}", UpdateRuleAsync);
        group.MapDelete("/alert-rules/{ruleId:long}", DeleteRuleAsync);
        group.MapPost("/alert-rules/{ruleId:long}/test", TestRuleAsync);
        group.MapGet("/projects/{projectId:long}/alert-notifications", ListNotificationsAsync);

        return group;
    }

    private static async Task<IResult> ListRulesAsync(
        long projectId,
        ManagementAlertStore store,
        ProjectStore projects,
        CancellationToken cancellationToken)
    {
        if (!await projects.ExistsAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return ManagementHttp.NotFound($"Project {projectId} was not found.");
        }

        var rules = await store.ListRulesAsync(projectId, cancellationToken).ConfigureAwait(false);

        return ManagementHttp.Json(
            new AlertRuleListResponse(rules),
            ProcessorJsonContext.Default.AlertRuleListResponse);
    }

    private static async Task<IResult> CreateRuleAsync(
        long projectId,
        HttpRequest request,
        ManagementAlertStore store,
        ProjectStore projects,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.CreateAlertRuleRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return ManagementHttp.BadRequest("A JSON body with the alert rule is required.");
        }

        if (!await projects.ExistsAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return ManagementHttp.NotFound($"Project {projectId} was not found.");
        }

        if (!TryNormalize(
                body.Name,
                body.Trigger,
                body.MinLevel,
                body.Environment,
                body.Threshold,
                body.WindowMinutes,
                body.CooldownMinutes,
                body.Channel,
                body.TargetUrl,
                out var fields,
                out var error))
        {
            return ManagementHttp.BadRequest(error!);
        }

        var created = await store
            .CreateRuleAsync(
                projectId,
                new AlertRuleCreate(
                    fields.Name,
                    fields.Trigger,
                    fields.MinLevel,
                    fields.Environment,
                    fields.Threshold,
                    fields.WindowMinutes,
                    fields.CooldownMinutes,
                    fields.Channel,
                    fields.TargetUrl,
                    body.IsEnabled ?? true),
                cancellationToken)
            .ConfigureAwait(false);

        return created is null
            ? ManagementHttp.NotFound($"Project {projectId} was not found.")
            : ManagementHttp.Json(
                created,
                ProcessorJsonContext.Default.AlertRuleResponse,
                StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateRuleAsync(
        long ruleId,
        HttpRequest request,
        ManagementAlertStore store,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.UpdateAlertRuleRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return ManagementHttp.BadRequest("A JSON body with the fields to change is required.");
        }

        var current = await store.GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return ManagementHttp.NotFound($"Alert rule {ruleId} was not found.");
        }

        if (!TryReadPatch(body, out var patch, out var readError))
        {
            return ManagementHttp.BadRequest(readError!);
        }

        if (!TryNormalize(
                patch.Name.IsSpecified ? patch.Name.Value : current.Name,
                patch.Trigger.IsSpecified ? patch.Trigger.Value : current.Trigger,
                patch.MinLevel.IsSpecified ? patch.MinLevel.Value : current.MinLevel,
                patch.Environment.IsSpecified ? patch.Environment.Value : current.Environment,
                patch.Threshold.IsSpecified ? patch.Threshold.Value : current.Threshold,
                patch.WindowMinutes.IsSpecified ? patch.WindowMinutes.Value : current.WindowMinutes,
                patch.CooldownMinutes.IsSpecified ? patch.CooldownMinutes.Value : current.CooldownMinutes,
                patch.Channel.IsSpecified ? patch.Channel.Value : current.Channel,
                patch.TargetUrl.IsSpecified ? patch.TargetUrl.Value : current.TargetUrl,
                out var fields,
                out var normalizationError))
        {
            return ManagementHttp.BadRequest(normalizationError!);
        }

        var update = new AlertRuleUpdate(
            patch.Name.IsSpecified ? OptionalField<string>.Specified(fields.Name) : OptionalField<string>.Unspecified,
            patch.Trigger.IsSpecified ? OptionalField<string>.Specified(fields.Trigger) : OptionalField<string>.Unspecified,
            patch.MinLevel.IsSpecified ? OptionalField<string?>.Specified(fields.MinLevel) : OptionalField<string?>.Unspecified,
            patch.Environment.IsSpecified ? OptionalField<string?>.Specified(fields.Environment) : OptionalField<string?>.Unspecified,
            patch.Threshold.IsSpecified ? OptionalField<int?>.Specified(fields.Threshold) : OptionalField<int?>.Unspecified,
            patch.WindowMinutes.IsSpecified ? OptionalField<int?>.Specified(fields.WindowMinutes) : OptionalField<int?>.Unspecified,
            patch.CooldownMinutes.IsSpecified ? OptionalField<int>.Specified(fields.CooldownMinutes) : OptionalField<int>.Unspecified,
            patch.Channel.IsSpecified ? OptionalField<string>.Specified(fields.Channel) : OptionalField<string>.Unspecified,
            patch.TargetUrl.IsSpecified ? OptionalField<string>.Specified(fields.TargetUrl) : OptionalField<string>.Unspecified,
            patch.IsEnabled);

        var updated = await store.UpdateRuleAsync(ruleId, update, cancellationToken).ConfigureAwait(false);

        return updated is null
            ? ManagementHttp.NotFound($"Alert rule {ruleId} was not found.")
            : ManagementHttp.Json(updated, ProcessorJsonContext.Default.AlertRuleResponse);
    }

    private static async Task<IResult> DeleteRuleAsync(
        long ruleId,
        ManagementAlertStore store,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);

        return deleted
            ? Results.NoContent()
            : ManagementHttp.NotFound($"Alert rule {ruleId} was not found.");
    }

    private static async Task<IResult> TestRuleAsync(
        long ruleId,
        ManagementAlertStore store,
        ProjectStore projects,
        AlertDispatcher dispatcher,
        IOptions<AlertOptions> options,
        CancellationToken cancellationToken)
    {
        var rule = await store.GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return ManagementHttp.NotFound($"Alert rule {ruleId} was not found.");
        }

        var project = await projects.GetAsync(rule.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return ManagementHttp.NotFound($"Project {rule.ProjectId} was not found.");
        }

        // The sample payload is built in memory and sent directly: the queue
        // is not touched and nothing is recorded for the test delivery.
        var now = DateTimeOffset.UtcNow;
        var payload = AlertPayloads.Create(
            TestTrigger,
            new AlertProject(project.Id, project.Name, project.Slug),
            new AlertIssue(
                Id: 0,
                Title: "Test alert from glitch-port",
                Culprit: null,
                Level: rule.MinLevel ?? "error",
                Status: "unresolved",
                EventCount: 0,
                FirstSeen: now,
                LastSeen: now,
                FirstRelease: null,
                LastRelease: null,
                Environment: rule.Environment),
            new AlertRuleRef(rule.Id, rule.Name),
            options.Value.DashboardBaseUrl);

        var result = await dispatcher
            .SendAsync(rule.Channel, rule.TargetUrl, payload, cancellationToken)
            .ConfigureAwait(false);

        return ManagementHttp.Json(
            new AlertTestResponse(result.Delivered, result.StatusCode, result.Error),
            ProcessorJsonContext.Default.AlertTestResponse);
    }

    private static async Task<IResult> ListNotificationsAsync(
        long projectId,
        string? limit,
        ManagementAlertStore store,
        ProjectStore projects,
        CancellationToken cancellationToken)
    {
        if (!await projects.ExistsAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return ManagementHttp.NotFound($"Project {projectId} was not found.");
        }

        var pageSize = DefaultNotificationLimit;
        if (limit is not null
            && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize is < 1 or > MaxNotificationLimit))
        {
            return ManagementHttp.BadRequest(
                $"limit must be between 1 and {MaxNotificationLimit}.");
        }

        var notifications = await store
            .ListNotificationsAsync(projectId, pageSize, cancellationToken)
            .ConfigureAwait(false);

        return ManagementHttp.Json(
            new AlertNotificationListResponse(notifications),
            ProcessorJsonContext.Default.AlertNotificationListResponse);
    }

    /// <summary>
    /// Normalizes and validates the complete rule values (a create body or the
    /// merged result of a patch).
    /// </summary>
    private static bool TryNormalize(
        string? name,
        string? trigger,
        string? minLevel,
        string? environment,
        int? threshold,
        int? windowMinutes,
        int? cooldownMinutes,
        string? channel,
        string? targetUrl,
        out AlertRuleFields fields,
        out string? error)
    {
        fields = default!;
        error = null;

        var normalizedName = name?.Trim();
        if (string.IsNullOrEmpty(normalizedName))
        {
            error = "name is required.";
            return false;
        }

        if (normalizedName.Length > MaxNameLength)
        {
            error = $"name must be at most {MaxNameLength} characters.";
            return false;
        }

        var normalizedTrigger = trigger?.Trim().ToLowerInvariant();
        if (normalizedTrigger is null || Array.IndexOf(Triggers, normalizedTrigger) < 0)
        {
            error = "trigger must be one of new_issue, regression or event_frequency.";
            return false;
        }

        string? normalizedLevel = null;
        if (!string.IsNullOrWhiteSpace(minLevel))
        {
            normalizedLevel = minLevel.Trim().ToLowerInvariant();
            if (!AlertLevels.IsKnown(normalizedLevel))
            {
                error = "min_level must be one of debug, info, warning, error or fatal.";
                return false;
            }
        }

        var normalizedEnvironment = string.IsNullOrWhiteSpace(environment) ? null : environment.Trim();

        var normalizedChannel = channel?.Trim().ToLowerInvariant();
        if (normalizedChannel is null
            || Array.IndexOf(AlertFormatters.Channels, normalizedChannel) < 0)
        {
            error = "channel must be one of webhook, discord or slack.";
            return false;
        }

        var normalizedUrl = targetUrl?.Trim();
        if (string.IsNullOrEmpty(normalizedUrl))
        {
            error = "target_url is required.";
            return false;
        }

        if (normalizedUrl.Length > MaxTargetUrlLength)
        {
            error = $"target_url must be at most {MaxTargetUrlLength} characters.";
            return false;
        }

        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "target_url must be an absolute http or https URL.";
            return false;
        }

        var normalizedCooldown = cooldownMinutes ?? DefaultCooldownMinutes;
        if (normalizedCooldown is < 1 or > 10080)
        {
            error = "cooldown_minutes must be between 1 and 10080.";
            return false;
        }

        if (normalizedTrigger == AlertTriggers.EventFrequency)
        {
            if (threshold is null || windowMinutes is null)
            {
                error = "event_frequency rules require threshold and window_minutes.";
                return false;
            }

            if (threshold < 1)
            {
                error = "threshold must be at least 1.";
                return false;
            }

            if (windowMinutes is < 1 or > 1440)
            {
                error = "window_minutes must be between 1 and 1440.";
                return false;
            }
        }
        else if (threshold is not null || windowMinutes is not null)
        {
            error = "threshold and window_minutes are only allowed for event_frequency rules.";
            return false;
        }

        fields = new AlertRuleFields(
            normalizedName,
            normalizedTrigger,
            normalizedLevel,
            normalizedEnvironment,
            threshold,
            windowMinutes,
            normalizedCooldown,
            normalizedChannel,
            normalizedUrl);

        return true;
    }

    private static bool TryReadPatch(
        UpdateAlertRuleRequest body,
        out AlertRulePatch patch,
        out string? error)
    {
        patch = default!;

        if (!TryReadRequiredString(body.Name, "name", out var name, out error))
        {
            return false;
        }

        if (!TryReadRequiredString(body.Trigger, "trigger", out var trigger, out error))
        {
            return false;
        }

        if (!TryReadNullableString(body.MinLevel, "min_level", out var minLevel, out error))
        {
            return false;
        }

        if (!TryReadNullableString(body.Environment, "environment", out var environment, out error))
        {
            return false;
        }

        if (!TryReadNullableInt(body.Threshold, "threshold", out var threshold, out error))
        {
            return false;
        }

        if (!TryReadNullableInt(body.WindowMinutes, "window_minutes", out var windowMinutes, out error))
        {
            return false;
        }

        if (!TryReadRequiredInt(body.CooldownMinutes, "cooldown_minutes", out var cooldownMinutes, out error))
        {
            return false;
        }

        if (!TryReadRequiredString(body.Channel, "channel", out var channel, out error))
        {
            return false;
        }

        if (!TryReadRequiredString(body.TargetUrl, "target_url", out var targetUrl, out error))
        {
            return false;
        }

        if (!TryReadBoolean(body.IsEnabled, "is_enabled", out var isEnabled, out error))
        {
            return false;
        }

        patch = new AlertRulePatch(
            name,
            trigger,
            minLevel,
            environment,
            threshold,
            windowMinutes,
            cooldownMinutes,
            channel,
            targetUrl,
            isEnabled);

        return true;
    }

    private static bool TryReadRequiredString(
        JsonElement element,
        string field,
        out OptionalField<string> value,
        out string? error)
    {
        value = OptionalField<string>.Unspecified;
        error = null;

        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"{field} must be a string.";
            return false;
        }

        value = OptionalField<string>.Specified(element.GetString()!);
        return true;
    }

    private static bool TryReadNullableString(
        JsonElement element,
        string field,
        out OptionalField<string?> value,
        out string? error)
    {
        value = OptionalField<string?>.Unspecified;
        error = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
                return true;

            case JsonValueKind.Null:
                value = OptionalField<string?>.Specified(null);
                return true;

            case JsonValueKind.String:
                value = OptionalField<string?>.Specified(element.GetString());
                return true;

            default:
                error = $"{field} must be a string or null.";
                return false;
        }
    }

    private static bool TryReadRequiredInt(
        JsonElement element,
        string field,
        out OptionalField<int> value,
        out string? error)
    {
        value = OptionalField<int>.Unspecified;
        error = null;

        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var parsed))
        {
            error = $"{field} must be an integer.";
            return false;
        }

        value = OptionalField<int>.Specified(parsed);
        return true;
    }

    private static bool TryReadNullableInt(
        JsonElement element,
        string field,
        out OptionalField<int?> value,
        out string? error)
    {
        value = OptionalField<int?>.Unspecified;
        error = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
                return true;

            case JsonValueKind.Null:
                value = OptionalField<int?>.Specified(null);
                return true;

            case JsonValueKind.Number when element.TryGetInt32(out var parsed):
                value = OptionalField<int?>.Specified(parsed);
                return true;

            default:
                error = $"{field} must be an integer or null.";
                return false;
        }
    }

    private static bool TryReadBoolean(
        JsonElement element,
        string field,
        out OptionalField<bool> value,
        out string? error)
    {
        value = OptionalField<bool>.Unspecified;
        error = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
                return true;

            case JsonValueKind.True:
                value = OptionalField<bool>.Specified(true);
                return true;

            case JsonValueKind.False:
                value = OptionalField<bool>.Specified(false);
                return true;

            default:
                error = $"{field} must be a boolean.";
                return false;
        }
    }

    /// <summary>The optional fields of a partial rule update.</summary>
    private sealed record AlertRulePatch(
        OptionalField<string> Name,
        OptionalField<string> Trigger,
        OptionalField<string?> MinLevel,
        OptionalField<string?> Environment,
        OptionalField<int?> Threshold,
        OptionalField<int?> WindowMinutes,
        OptionalField<int> CooldownMinutes,
        OptionalField<string> Channel,
        OptionalField<string> TargetUrl,
        OptionalField<bool> IsEnabled);

    /// <summary>The normalized values of a complete alert rule.</summary>
    private sealed record AlertRuleFields(
        string Name,
        string Trigger,
        string? MinLevel,
        string? Environment,
        int? Threshold,
        int? WindowMinutes,
        int CooldownMinutes,
        string Channel,
        string TargetUrl);
}
