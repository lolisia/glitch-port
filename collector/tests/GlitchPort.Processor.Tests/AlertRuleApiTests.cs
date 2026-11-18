using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlitchPort.Processor;
using GlitchPort.Processor.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// The alert rule management API: CRUD, validation, the delivery test endpoint,
/// the notification listing and the <c>Processor:Alerts</c> options.
/// </summary>
[Collection(ProcessorCollection.Name)]
public sealed class AlertRuleApiTests
{
    private const string InvalidProjectId = "9000001";

    private readonly ProcessorFixture _fixture;

    public AlertRuleApiTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Rule_crud_round_trip()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            using var api = new ManagementApiClient(_fixture);

            using var created = await api.PostAsync(
                RulesPath(projectId),
                """
                {
                  "name": "errors in production",
                  "trigger": "new_issue",
                  "min_level": "error",
                  "environment": "production",
                  "channel": "webhook",
                  "target_url": "https://alerts.example.com/hook",
                  "is_enabled": false
                }
                """);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var rule = await ManagementApiData.ReadJsonAsync(created);
            var ruleId = rule.GetProperty("id").GetInt64();

            Assert.Equal(projectId, rule.GetProperty("project_id").GetInt64());
            Assert.Equal("errors in production", rule.GetProperty("name").GetString());
            Assert.Equal("new_issue", rule.GetProperty("trigger").GetString());
            Assert.Equal("error", rule.GetProperty("min_level").GetString());
            Assert.Equal("production", rule.GetProperty("environment").GetString());
            Assert.Equal(JsonValueKind.Null, rule.GetProperty("threshold").ValueKind);
            Assert.Equal(JsonValueKind.Null, rule.GetProperty("window_minutes").ValueKind);
            Assert.Equal(60, rule.GetProperty("cooldown_minutes").GetInt32());
            Assert.Equal("webhook", rule.GetProperty("channel").GetString());
            Assert.Equal("https://alerts.example.com/hook", rule.GetProperty("target_url").GetString());
            Assert.False(rule.GetProperty("is_enabled").GetBoolean());

            var createdAt = rule.GetProperty("created_at").GetDateTimeOffset();

            using var list = await api.GetAsync(RulesPath(projectId));
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var items = (await ManagementApiData.ReadJsonAsync(list)).GetProperty("items");
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal(ruleId, items[0].GetProperty("id").GetInt64());

            using var patched = await api.PatchAsync(
                $"/api/manage/alert-rules/{ruleId}",
                """
                {
                  "name": "renamed rule",
                  "min_level": null,
                  "is_enabled": true
                }
                """);

            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            var updated = await ManagementApiData.ReadJsonAsync(patched);
            Assert.Equal(ruleId, updated.GetProperty("id").GetInt64());
            Assert.Equal("renamed rule", updated.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, updated.GetProperty("min_level").ValueKind);
            Assert.True(updated.GetProperty("is_enabled").GetBoolean());
            Assert.Equal("https://alerts.example.com/hook", updated.GetProperty("target_url").GetString());
            Assert.True(updated.GetProperty("updated_at").GetDateTimeOffset() >= createdAt);

            // Switching to event_frequency without a threshold and window is
            // rejected by the validation of the merged rule.
            using var invalidPatch = await api.PatchAsync(
                $"/api/manage/alert-rules/{ruleId}",
                """{ "trigger": "event_frequency" }""");
            Assert.Equal(HttpStatusCode.BadRequest, invalidPatch.StatusCode);

            using var deleted = await api.DeleteAsync($"/api/manage/alert-rules/{ruleId}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            using var afterDelete = await api.GetAsync(RulesPath(projectId));
            Assert.Equal(
                0,
                (await ManagementApiData.ReadJsonAsync(afterDelete)).GetProperty("items").GetArrayLength());

            using var deletedAgain = await api.DeleteAsync($"/api/manage/alert-rules/{ruleId}");
            Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);

            using var patchMissing = await api.PatchAsync(
                $"/api/manage/alert-rules/{ruleId}",
                """{ "name": "gone" }""");
            Assert.Equal(HttpStatusCode.NotFound, patchMissing.StatusCode);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Theory]
    [InlineData("""{ "name": "x", "trigger": "explode", "channel": "webhook", "target_url": "https://example.com/hook" }""")]
    [InlineData("""{ "name": "x", "trigger": "new_issue", "channel": "email", "target_url": "https://example.com/hook" }""")]
    [InlineData("""{ "name": "x", "trigger": "new_issue", "min_level": "critical", "channel": "webhook", "target_url": "https://example.com/hook" }""")]
    public async Task Create_rejects_unknown_trigger_channel_and_level(string json)
    {
        var projectId = TestData.NextProjectId();
        try
        {
            using var api = new ManagementApiClient(_fixture);

            using var response = await api.PostAsync(RulesPath(projectId), json);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Theory]
    [InlineData("""{ "name": "", "trigger": "new_issue", "channel": "webhook", "target_url": "https://example.com/hook" }""")]
    [InlineData("""{ "trigger": "new_issue", "channel": "webhook", "target_url": "https://example.com/hook" }""")]
    [InlineData("""{ "name": "x", "trigger": "new_issue", "channel": "webhook", "target_url": "not a url" }""")]
    [InlineData("""{ "name": "x", "trigger": "new_issue", "channel": "webhook", "target_url": "ftp://example.com/hook" }""")]
    [InlineData("""{ "name": "x", "trigger": "new_issue", "channel": "webhook" }""")]
    [InlineData("""{ "name": "x", "trigger": "new_issue", "cooldown_minutes": 0, "channel": "webhook", "target_url": "https://example.com/hook" }""")]
    [InlineData("""{ "name": "x", "trigger": "new_issue", "cooldown_minutes": 10081, "channel": "webhook", "target_url": "https://example.com/hook" }""")]
    public async Task Create_validates_the_name_url_and_ranges(string json)
    {
        var projectId = TestData.NextProjectId();
        try
        {
            using var api = new ManagementApiClient(_fixture);

            using var response = await api.PostAsync(RulesPath(projectId), json);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Create_rejects_a_name_longer_than_100_characters()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            using var api = new ManagementApiClient(_fixture);
            var body = new JsonObject
            {
                ["name"] = new string('n', 101),
                ["trigger"] = "new_issue",
                ["channel"] = "webhook",
                ["target_url"] = "https://example.com/hook",
            };

            using var response = await api.PostAsync(RulesPath(projectId), body.ToJsonString());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Create_rejects_a_target_url_longer_than_2048_characters()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            using var api = new ManagementApiClient(_fixture);
            var body = new JsonObject
            {
                ["name"] = "long url",
                ["trigger"] = "new_issue",
                ["channel"] = "webhook",
                ["target_url"] = "https://example.com/" + new string('a', 2048),
            };

            using var response = await api.PostAsync(RulesPath(projectId), body.ToJsonString());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Event_frequency_requires_a_threshold_and_window_and_other_triggers_reject_them()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            using var api = new ManagementApiClient(_fixture);

            foreach (var invalid in new[]
                     {
                         """{ "name": "f", "trigger": "event_frequency", "channel": "webhook", "target_url": "https://example.com/hook" }""",
                         """{ "name": "f", "trigger": "event_frequency", "threshold": 3, "channel": "webhook", "target_url": "https://example.com/hook" }""",
                         """{ "name": "f", "trigger": "event_frequency", "window_minutes": 10, "channel": "webhook", "target_url": "https://example.com/hook" }""",
                         """{ "name": "f", "trigger": "event_frequency", "threshold": 0, "window_minutes": 10, "channel": "webhook", "target_url": "https://example.com/hook" }""",
                         """{ "name": "f", "trigger": "event_frequency", "threshold": 3, "window_minutes": 0, "channel": "webhook", "target_url": "https://example.com/hook" }""",
                         """{ "name": "f", "trigger": "event_frequency", "threshold": 3, "window_minutes": 1441, "channel": "webhook", "target_url": "https://example.com/hook" }""",
                         """{ "name": "n", "trigger": "new_issue", "threshold": 3, "channel": "webhook", "target_url": "https://example.com/hook" }""",
                         """{ "name": "n", "trigger": "new_issue", "window_minutes": 10, "channel": "webhook", "target_url": "https://example.com/hook" }""",
                         """{ "name": "n", "trigger": "regression", "threshold": 1, "window_minutes": 1, "channel": "webhook", "target_url": "https://example.com/hook" }""",
                     })
            {
                using var response = await api.PostAsync(RulesPath(projectId), invalid);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            using var valid = await api.PostAsync(
                RulesPath(projectId),
                """
                {
                  "name": "frequent",
                  "trigger": "event_frequency",
                  "threshold": 10,
                  "window_minutes": 30,
                  "channel": "slack",
                  "target_url": "https://hooks.slack.com/services/example"
                }
                """);

            Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
            var rule = await ManagementApiData.ReadJsonAsync(valid);
            Assert.Equal(10, rule.GetProperty("threshold").GetInt32());
            Assert.Equal(30, rule.GetProperty("window_minutes").GetInt32());
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Missing_projects_and_rules_return_404()
    {
        using var api = new ManagementApiClient(_fixture);

        using var list = await api.GetAsync($"/api/manage/projects/{InvalidProjectId}/alert-rules");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);

        using var create = await api.PostAsync(
            $"/api/manage/projects/{InvalidProjectId}/alert-rules",
            """
            {
              "name": "orphan",
              "trigger": "new_issue",
              "channel": "webhook",
              "target_url": "https://example.com/hook"
            }
            """);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);

        using var notifications = await api.GetAsync(
            $"/api/manage/projects/{InvalidProjectId}/alert-notifications");
        Assert.Equal(HttpStatusCode.NotFound, notifications.StatusCode);

        using var test = await api.PostAsync(
            $"/api/manage/alert-rules/{InvalidProjectId}/test",
            "{}");
        Assert.Equal(HttpStatusCode.NotFound, test.StatusCode);
    }

    [Fact]
    public async Task Test_endpoint_delivers_the_sample_without_queueing()
    {
        var projectId = TestData.NextProjectId();
        var handler = new RecordingHttpMessageHandler(_ => AlertTestSupport.Ok());

        try
        {
            using var factory = new ProcessorApplicationFactory(
                _fixture.ConnectionString,
                alertsHandler: handler);
            using var client = factory.CreateClient();

            var ruleId = await CreateRuleAsync(
                client,
                projectId,
                "webhook",
                "https://alerts.example.com/test");

            using var response = await client.SendAsync(
                Authorized(HttpMethod.Post, $"/api/manage/alert-rules/{ruleId}/test"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await ManagementApiData.ReadJsonAsync(response);
            Assert.True(result.GetProperty("delivered").GetBoolean());
            Assert.Equal(200, result.GetProperty("status_code").GetInt32());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://alerts.example.com/test", request.Url);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("test", body.RootElement.GetProperty("trigger").GetString());
            Assert.Equal(ruleId, body.RootElement.GetProperty("rule").GetProperty("id").GetInt64());

            // The test delivery never touches the queue.
            Assert.Equal(
                0,
                await AlertTestSupport.CountNotificationsAsync(_fixture.ConnectionString, projectId));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Test_endpoint_reports_a_failed_delivery()
    {
        var projectId = TestData.NextProjectId();
        var handler = new RecordingHttpMessageHandler(
            _ => AlertTestSupport.Status(HttpStatusCode.InternalServerError));

        try
        {
            using var factory = new ProcessorApplicationFactory(
                _fixture.ConnectionString,
                alertsHandler: handler);
            using var client = factory.CreateClient();

            var ruleId = await CreateRuleAsync(
                client,
                projectId,
                "discord",
                "https://discord.example.com/api/webhooks/example");

            using var response = await client.SendAsync(
                Authorized(HttpMethod.Post, $"/api/manage/alert-rules/{ruleId}/test"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await ManagementApiData.ReadJsonAsync(response);
            Assert.False(result.GetProperty("delivered").GetBoolean());
            Assert.Equal(500, result.GetProperty("status_code").GetInt32());
            Assert.Equal("500", result.GetProperty("error").GetString());
            Assert.Equal(
                0,
                await AlertTestSupport.CountNotificationsAsync(_fixture.ConnectionString, projectId));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Notifications_are_listed_with_the_rule_name()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            using var api = new ManagementApiClient(_fixture);
            await CreateRuleAsync(api.Raw, projectId, "webhook", "https://alerts.example.com/hook");

            await ManagementApiData.IngestEventAsync(
                api.Raw,
                projectId,
                new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
                "fingerprint-notification-list");

            using var response = await api.GetAsync(
                $"/api/manage/projects/{projectId}/alert-notifications?limit=50");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var items = (await ManagementApiData.ReadJsonAsync(response)).GetProperty("items");
            Assert.Equal(1, items.GetArrayLength());

            var item = items[0];
            Assert.True(item.GetProperty("id").GetInt64() > 0);
            Assert.True(item.GetProperty("rule_id").GetInt64() > 0);
            Assert.Equal("alert rule", item.GetProperty("rule_name").GetString());
            Assert.True(item.GetProperty("issue_id").GetInt64() > 0);
            Assert.Equal("new_issue", item.GetProperty("trigger").GetString());
            Assert.Equal("pending", item.GetProperty("status").GetString());
            Assert.Equal(0, item.GetProperty("attempts").GetInt32());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("last_error").ValueKind);
            Assert.True(item.GetProperty("created_at").GetDateTimeOffset() > DateTimeOffset.MinValue);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("sent_at").ValueKind);

            using var invalidLimit = await api.GetAsync(
                $"/api/manage/projects/{projectId}/alert-notifications?limit=101");
            Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Alert_endpoints_require_the_admin_token()
    {
        using var api = new ManagementApiClient(_fixture);

        using var response = await api.GetAsync(
            $"/api/manage/projects/{TestData.DefaultProjectId}/alert-rules",
            token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Alert_options_expose_the_documented_defaults()
    {
        var options = new AlertOptions();

        Assert.True(options.Enabled);
        Assert.Equal(60, options.EvaluateIntervalSeconds);
        Assert.Equal(5, options.DispatchIntervalSeconds);
        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(10, options.HttpTimeoutSeconds);
        Assert.Equal(string.Empty, options.DashboardBaseUrl);
        Assert.Equal(30, options.RetentionDays);
    }

    [Fact]
    public void Alert_options_fall_back_to_the_defaults_for_invalid_values()
    {
        var options = new AlertOptions
        {
            EvaluateIntervalSeconds = 0,
            DispatchIntervalSeconds = -1,
            MaxAttempts = 0,
            HttpTimeoutSeconds = 0,
            RetentionDays = -5,
        };

        Assert.Equal(60, options.EvaluateIntervalSeconds);
        Assert.Equal(5, options.DispatchIntervalSeconds);
        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(10, options.HttpTimeoutSeconds);
        Assert.Equal(30, options.RetentionDays);
    }

    [Fact]
    public void The_test_host_binds_the_alerts_section()
    {
        var options = _fixture.Factory.Services.GetRequiredService<IOptions<AlertOptions>>().Value;

        // ProcessorApplicationFactory disables the workers for the shared host;
        // queueing keeps working, which the enqueue tests prove.
        Assert.False(options.Enabled);

        var processor = _fixture.Factory.Services
            .GetRequiredService<IOptions<ProcessorOptions>>()
            .Value;
        Assert.Equal(ProcessorApplicationFactory.AdminToken, processor.AdminToken);
    }

    private static async Task<long> CreateRuleAsync(
        HttpClient client,
        long projectId,
        string channel,
        string targetUrl)
    {
        using var response = await client.SendAsync(
            Authorized(
                HttpMethod.Post,
                RulesPath(projectId),
                $$"""
                {
                  "name": "alert rule",
                  "trigger": "new_issue",
                  "channel": "{{channel}}",
                  "target_url": "{{targetUrl}}"
                }
                """));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ManagementApiData.ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ProcessorApplicationFactory.AdminToken);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static string RulesPath(long projectId) =>
        $"/api/manage/projects/{projectId}/alert-rules";
}
