using System.Text.Json;
using GlitchPort.Processor.Alerts;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// The JSON shapes produced for the webhook, Discord and Slack channels.
/// </summary>
public sealed class AlertFormatterTests
{
    [Fact]
    public void Webhook_emits_the_common_payload()
    {
        var payload = AlertTestSupport.CreatePayload();

        using var document = JsonDocument.Parse(AlertFormatters.Format("webhook", payload));
        var root = document.RootElement;

        Assert.Equal("new_issue", root.GetProperty("trigger").GetString());

        var project = root.GetProperty("project");
        Assert.Equal(7, project.GetProperty("id").GetInt64());
        Assert.Equal("Star Raiders", project.GetProperty("name").GetString());
        Assert.Equal("star-raiders", project.GetProperty("slug").GetString());

        var issue = root.GetProperty("issue");
        Assert.Equal(42, issue.GetProperty("id").GetInt64());
        Assert.Equal("NullReferenceException: Object reference not set", issue.GetProperty("title").GetString());
        Assert.Equal("Weapon.Fire", issue.GetProperty("culprit").GetString());
        Assert.Equal("error", issue.GetProperty("level").GetString());
        Assert.Equal("unresolved", issue.GetProperty("status").GetString());
        Assert.Equal(12, issue.GetProperty("event_count").GetInt64());
        Assert.Equal("star-raiders@1.0.0", issue.GetProperty("first_release").GetString());
        Assert.Equal("star-raiders@1.2.3", issue.GetProperty("last_release").GetString());
        Assert.Equal("production", issue.GetProperty("environment").GetString());
        Assert.Equal(
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
            issue.GetProperty("first_seen").GetDateTimeOffset());
        Assert.Equal(
            new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.Zero),
            issue.GetProperty("last_seen").GetDateTimeOffset());

        var rule = root.GetProperty("rule");
        Assert.Equal(3, rule.GetProperty("id").GetInt64());
        Assert.Equal("Notify on new issues", rule.GetProperty("name").GetString());

        Assert.Equal(
            "https://dashboard.example.com#/issues/42",
            root.GetProperty("url").GetString());
    }

    [Fact]
    public void Webhook_keeps_a_null_link_when_no_dashboard_is_configured()
    {
        var payload = AlertTestSupport.CreatePayload(url: null);

        using var document = JsonDocument.Parse(AlertFormatters.Webhook(payload));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("url").ValueKind);
    }

    [Fact]
    public void Discord_emits_a_valid_embed()
    {
        var payload = AlertTestSupport.CreatePayload();

        using var document = JsonDocument.Parse(AlertFormatters.Format("discord", payload));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("content").ValueKind);

        var embeds = root.GetProperty("embeds");
        Assert.Equal(1, embeds.GetArrayLength());

        var embed = embeds[0];
        Assert.Equal(
            "NullReferenceException: Object reference not set",
            embed.GetProperty("title").GetString());
        Assert.Contains("New issue", embed.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("Star Raiders", embed.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            "https://dashboard.example.com#/issues/42",
            embed.GetProperty("url").GetString());
        Assert.Equal(AlertFormatters.ColorForLevel("error"), embed.GetProperty("color").GetInt32());

        // Discord requires every field to carry a non-empty name and value.
        var fields = embed.GetProperty("fields");
        Assert.True(fields.GetArrayLength() >= 2);
        foreach (var field in fields.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(field.GetProperty("value").GetString()));
        }

        Assert.Contains(
            fields.EnumerateArray(),
            field => field.GetProperty("name").GetString() == "Level"
                && field.GetProperty("value").GetString() == "error");
        Assert.Contains(
            fields.EnumerateArray(),
            field => field.GetProperty("name").GetString() == "Environment"
                && field.GetProperty("value").GetString() == "production");
        Assert.Contains(
            fields.EnumerateArray(),
            field => field.GetProperty("name").GetString() == "Rule"
                && field.GetProperty("value").GetString() == "Notify on new issues");
    }

    [Fact]
    public void Discord_keeps_a_null_link_when_no_dashboard_is_configured()
    {
        var payload = AlertTestSupport.CreatePayload(url: null);

        using var document = JsonDocument.Parse(AlertFormatters.Discord(payload));
        var embed = document.RootElement.GetProperty("embeds")[0];

        Assert.Equal(JsonValueKind.Null, embed.GetProperty("url").ValueKind);
    }

    [Fact]
    public void Discord_colors_are_distinct_per_level()
    {
        var colors = new[] { "debug", "info", "warning", "error", "fatal" }
            .Select(AlertFormatters.ColorForLevel)
            .ToArray();

        Assert.Equal(colors.Length, colors.Distinct().Count());
    }

    [Fact]
    public void Slack_emits_a_summary_section_and_context()
    {
        var payload = AlertTestSupport.CreatePayload();

        using var document = JsonDocument.Parse(AlertFormatters.Format("slack", payload));
        var root = document.RootElement;

        Assert.Equal(
            "New issue: NullReferenceException: Object reference not set (error)",
            root.GetProperty("text").GetString());

        var blocks = root.GetProperty("blocks");
        Assert.Equal(2, blocks.GetArrayLength());

        var section = blocks[0];
        Assert.Equal("section", section.GetProperty("type").GetString());
        Assert.Equal("mrkdwn", section.GetProperty("text").GetProperty("type").GetString());
        var sectionText = section.GetProperty("text").GetProperty("text").GetString();
        Assert.Contains("NullReferenceException", sectionText, StringComparison.Ordinal);
        Assert.Contains("Star Raiders", sectionText, StringComparison.Ordinal);
        Assert.Contains(
            "https://dashboard.example.com#/issues/42",
            sectionText,
            StringComparison.Ordinal);

        var context = blocks[1];
        Assert.Equal("context", context.GetProperty("type").GetString());
        var elements = context.GetProperty("elements");
        Assert.Equal(1, elements.GetArrayLength());
        Assert.Equal("mrkdwn", elements[0].GetProperty("type").GetString());
        Assert.Contains(
            "Notify on new issues",
            elements[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Slack_omits_the_link_when_no_dashboard_is_configured()
    {
        var payload = AlertTestSupport.CreatePayload(url: null);

        using var document = JsonDocument.Parse(AlertFormatters.Slack(payload));
        var sectionText = document.RootElement
            .GetProperty("blocks")[0]
            .GetProperty("text")
            .GetProperty("text")
            .GetString();

        Assert.DoesNotContain("Open in dashboard", sectionText, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_channels_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AlertFormatters.Format("email", AlertTestSupport.CreatePayload()));
    }
}
