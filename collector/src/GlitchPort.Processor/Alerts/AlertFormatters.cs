using System.Globalization;
using System.Text.Json.Nodes;

namespace GlitchPort.Processor.Alerts;

/// <summary>
/// Formats a stored <see cref="AlertPayload"/> for the channel of a rule.
/// </summary>
/// <remarks>
/// Formatting happens at send time from the payload stored in
/// <c>alert_notifications.payload</c>, so a notification is delivered with the
/// data it was queued with. The webhook body is the payload itself; the Discord
/// and Slack bodies follow the shape of those services (Discord embeds carry
/// <c>fields[].name</c> and <c>fields[].value</c> strings).
/// </remarks>
public static class AlertFormatters
{
    /// <summary>Channel name of a generic JSON webhook.</summary>
    public const string WebhookChannel = "webhook";

    /// <summary>Channel name of a Discord webhook.</summary>
    public const string DiscordChannel = "discord";

    /// <summary>Channel name of a Slack webhook.</summary>
    public const string SlackChannel = "slack";

    /// <summary>The channels accepted by <c>alert_rules.channel</c>.</summary>
    public static readonly string[] Channels = [WebhookChannel, DiscordChannel, SlackChannel];

    /// <summary>Formats <paramref name="payload"/> for <paramref name="channel"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The channel is not supported.</exception>
    public static string Format(string channel, AlertPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return channel switch
        {
            WebhookChannel => Webhook(payload),
            DiscordChannel => Discord(payload),
            SlackChannel => Slack(payload),
            _ => throw new ArgumentOutOfRangeException(
                nameof(channel),
                channel,
                "Unsupported alert channel."),
        };
    }

    /// <summary>
    /// Returns the webhook body: the payload object with the trigger, the
    /// project, the issue, the rule and the dashboard link.
    /// </summary>
    public static string Webhook(AlertPayload payload) => AlertPayloads.Serialize(payload);

    /// <summary>
    /// Returns the Discord webhook body:
    /// <c>{content:null, embeds:[{title, description, url, color, fields}]}</c>.
    /// </summary>
    public static string Discord(AlertPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var fields = new JsonArray
        {
            DiscordField("Level", payload.Issue.Level),
            DiscordField("Environment", payload.Issue.Environment ?? "(none)"),
            DiscordField(
                "Events",
                payload.Issue.EventCount.ToString(CultureInfo.InvariantCulture)),
            DiscordField("Rule", payload.Rule.Name),
        };

        var embed = new JsonObject
        {
            ["title"] = payload.Issue.Title,
            ["description"] = $"{TriggerLabel(payload.Trigger)} · {payload.Project.Name} ({payload.Project.Slug})",
            ["url"] = payload.Url,
            ["color"] = ColorForLevel(payload.Issue.Level),
            ["fields"] = fields,
        };

        var message = new JsonObject
        {
            ["content"] = null,
            ["embeds"] = new JsonArray(embed),
        };

        return message.ToJsonString();
    }

    /// <summary>
    /// Returns the Slack webhook body: a summary text plus a section block and
    /// a context block.
    /// </summary>
    public static string Slack(AlertPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var summary = $"{TriggerLabel(payload.Trigger)}: {payload.Issue.Title} ({payload.Issue.Level})";

        var sectionText = $"*{summary}*\nProject: {payload.Project.Name} ({payload.Project.Slug})";
        if (payload.Url is not null)
        {
            sectionText += $"\n<{payload.Url}|Open in dashboard>";
        }

        var contextText =
            $"Rule: {payload.Rule.Name} · Trigger: {payload.Trigger} · Environment: {payload.Issue.Environment ?? "(none)"}";

        var blocks = new JsonArray(
            new JsonObject
            {
                ["type"] = "section",
                ["text"] = new JsonObject
                {
                    ["type"] = "mrkdwn",
                    ["text"] = sectionText,
                },
            },
            new JsonObject
            {
                ["type"] = "context",
                ["elements"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "mrkdwn",
                        ["text"] = contextText,
                    }),
            });

        var message = new JsonObject
        {
            ["text"] = summary,
            ["blocks"] = blocks,
        };

        return message.ToJsonString();
    }

    /// <summary>
    /// Returns the Discord embed color of an issue level (the decimal RGB
    /// value Discord expects).
    /// </summary>
    public static int ColorForLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "debug" => 0x808080,
        "info" => 0x3498DB,
        "warning" => 0xE67E22,
        "error" => 0xE74C3C,
        "fatal" => 0x992D22,
        _ => 0x808080,
    };

    private static string TriggerLabel(string trigger) => trigger switch
    {
        AlertTriggers.NewIssue => "New issue",
        AlertTriggers.Regression => "Regression",
        AlertTriggers.EventFrequency => "Event frequency",
        _ => trigger,
    };

    private static JsonObject DiscordField(string name, string value) =>
        new()
        {
            ["name"] = name,
            ["value"] = value,
        };
}
