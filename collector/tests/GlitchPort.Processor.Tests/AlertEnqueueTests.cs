using System.Net;
using System.Text.Json;
using GlitchPort.Collector.Contracts;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// Alert notifications queued by the event store: the new_issue and regression
/// triggers, deduplication on resend, and the level and environment filters.
/// </summary>
[Collection(ProcessorCollection.Name)]
public sealed class AlertEnqueueTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ProcessorFixture _fixture;

    public AlertEnqueueTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task New_issue_is_queued_with_the_common_payload()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            var ruleId = await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                name: "notify new issues",
                trigger: "new_issue",
                minLevel: "error",
                environment: "production");

            using var client = _fixture.CreateClient();
            await IngestAsync(client, projectId, BaseTime, "fingerprint-new-issue");

            var notification = Assert.Single(
                await AlertTestSupport.GetNotificationsAsync(_fixture.ConnectionString, projectId));

            Assert.Equal(ruleId, notification.RuleId);
            Assert.Equal($"new_issue:{ruleId}:{notification.IssueId}", notification.DedupeKey);
            Assert.Equal("new_issue", notification.Trigger);
            Assert.Equal("pending", notification.Status);
            Assert.Equal(0, notification.Attempts);
            Assert.Null(notification.LastError);
            Assert.Null(notification.SentAt);

            using var payload = JsonDocument.Parse(notification.PayloadJson);
            var root = payload.RootElement;

            Assert.Equal("new_issue", root.GetProperty("trigger").GetString());

            var project = root.GetProperty("project");
            Assert.Equal(projectId, project.GetProperty("id").GetInt64());
            Assert.Equal($"Project {projectId}", project.GetProperty("name").GetString());
            Assert.Equal($"project-{projectId}", project.GetProperty("slug").GetString());

            var issue = root.GetProperty("issue");
            Assert.Equal(notification.IssueId, issue.GetProperty("id").GetInt64());
            Assert.False(string.IsNullOrEmpty(issue.GetProperty("title").GetString()));
            Assert.Equal("error", issue.GetProperty("level").GetString());
            Assert.Equal("unresolved", issue.GetProperty("status").GetString());
            Assert.Equal(1, issue.GetProperty("event_count").GetInt64());
            Assert.Equal("star-raiders@1.2.3", issue.GetProperty("first_release").GetString());
            Assert.Equal("star-raiders@1.2.3", issue.GetProperty("last_release").GetString());
            Assert.Equal("production", issue.GetProperty("environment").GetString());
            Assert.Equal(
                BaseTime,
                issue.GetProperty("first_seen").GetDateTimeOffset());
            Assert.Equal(
                BaseTime,
                issue.GetProperty("last_seen").GetDateTimeOffset());

            var rule = root.GetProperty("rule");
            Assert.Equal(ruleId, rule.GetProperty("id").GetInt64());
            Assert.Equal("notify new issues", rule.GetProperty("name").GetString());

            // The shared test host has no DashboardBaseUrl, so the link is null.
            Assert.Equal(JsonValueKind.Null, root.GetProperty("url").ValueKind);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Resending_an_event_does_not_queue_a_second_notification()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            await AlertTestSupport.InsertRuleAsync(_fixture.ConnectionString, projectId, trigger: "new_issue");

            var envelope = TestData.CreatePayloadEnvelope(
                Guid.NewGuid(),
                projectId,
                BaseTime,
                TestData.CreateExceptionPayload(fingerprint: ["fingerprint-resend"]));

            using var client = _fixture.CreateClient();
            Assert.Equal(1, await StoreAsync(client, envelope));
            Assert.Equal(
                1,
                await AlertTestSupport.CountNotificationsAsync(_fixture.ConnectionString, projectId));

            Assert.Equal(0, await StoreAsync(client, envelope));
            Assert.Equal(
                1,
                await AlertTestSupport.CountNotificationsAsync(_fixture.ConnectionString, projectId));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Regression_queues_one_notification_per_regression_count()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            var ruleId = await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                trigger: "regression");

            using var client = _fixture.CreateClient();

            // The first event creates the issue; that is not a regression.
            await IngestAsync(client, projectId, BaseTime, "fingerprint-regression");
            Assert.Equal(
                0,
                await AlertTestSupport.CountNotificationsAsync(_fixture.ConnectionString, projectId));

            var issueId = await TestDatabase.QuerySingleLongAsync(
                _fixture.ConnectionString,
                $"select id from issues where project_id = {projectId}");
            await ResolveAsync(issueId);

            var second = CreateEnvelope(projectId, BaseTime.AddHours(1), "fingerprint-regression");
            Assert.Equal(1, await StoreAsync(client, second));

            var firstRegression = Assert.Single(
                await AlertTestSupport.GetNotificationsAsync(_fixture.ConnectionString, projectId));
            Assert.Equal($"regression:{ruleId}:{issueId}:1", firstRegression.DedupeKey);
            Assert.Equal("regression", firstRegression.Trigger);

            // Storing the same event again must not open the issue a second
            // time, so the regression notification is not queued again.
            Assert.Equal(0, await StoreAsync(client, second));
            Assert.Equal(
                1,
                await AlertTestSupport.CountNotificationsAsync(_fixture.ConnectionString, projectId));

            await ResolveAsync(issueId);
            Assert.Equal(
                1,
                await StoreAsync(
                    client,
                    CreateEnvelope(projectId, BaseTime.AddHours(2), "fingerprint-regression")));

            var notifications = await AlertTestSupport.GetNotificationsAsync(
                _fixture.ConnectionString,
                projectId);
            Assert.Equal(2, notifications.Count);
            Assert.Contains(
                notifications,
                notification => notification.DedupeKey == $"regression:{ruleId}:{issueId}:2");
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Concurrent_batches_queue_the_new_issue_notification_once()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            var ruleId = await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                trigger: "new_issue");

            using var client = _fixture.CreateClient();

            // Five separate batches race to create the same issue. Exactly one
            // of them observes the creation, and the unique dedupe key keeps
            // the notification single even if more than one did.
            var stored = await Task.WhenAll(Enumerable.Range(0, 5).Select(index =>
                StoreAsync(
                    client,
                    CreateEnvelope(
                        projectId,
                        BaseTime.AddSeconds(index),
                        "fingerprint-concurrent-create"))));

            Assert.All(stored, count => Assert.Equal(1, count));
            Assert.Equal(
                1,
                await AlertTestSupport.CountNotificationsAsync(_fixture.ConnectionString, projectId));

            var notification = Assert.Single(
                await AlertTestSupport.GetNotificationsAsync(_fixture.ConnectionString, projectId));
            Assert.Equal($"new_issue:{ruleId}:{notification.IssueId}", notification.DedupeKey);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Level_and_environment_conditions_are_enforced()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            var strictRuleId = await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                name: "errors in production",
                trigger: "new_issue",
                minLevel: "error",
                environment: "production");

            using var client = _fixture.CreateClient();

            // A warning is below min_level.
            await IngestAsync(client, projectId, BaseTime, "fingerprint-warning", level: "warning");
            Assert.Equal(0, await CountForRuleAsync(strictRuleId));

            // An error in another environment does not match environment.
            await IngestAsync(client, projectId, BaseTime, "fingerprint-staging", environment: "staging");
            Assert.Equal(0, await CountForRuleAsync(strictRuleId));

            // An error in production matches.
            await IngestAsync(client, projectId, BaseTime, "fingerprint-match");
            Assert.Equal(1, await CountForRuleAsync(strictRuleId));

            // A rule without level or environment filters matches every new
            // issue, so the next event must queue for both rules.
            var looseRuleId = await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                name: "everything",
                trigger: "new_issue");

            await IngestAsync(
                client,
                projectId,
                BaseTime,
                "fingerprint-warning-staging",
                level: "warning",
                environment: "staging");

            Assert.Equal(1, await CountForRuleAsync(strictRuleId));
            Assert.Equal(1, await CountForRuleAsync(looseRuleId));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    private Task<long> CountForRuleAsync(long ruleId) =>
        TestDatabase.QuerySingleLongAsync(
            _fixture.ConnectionString,
            $"select count(*) from alert_notifications where rule_id = {ruleId}");

    private Task ResolveAsync(long issueId) =>
        TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"update issues set status = 'resolved', resolved_at = now() where id = {issueId}");

    private async Task IngestAsync(
        HttpClient client,
        long projectId,
        DateTimeOffset timestamp,
        string fingerprint,
        string level = "error",
        string? environment = "production")
    {
        var envelope = CreateEnvelope(projectId, timestamp, fingerprint, level, environment);
        Assert.Equal(1, await StoreAsync(client, envelope));
    }

    private static Envelope CreateEnvelope(
        long projectId,
        DateTimeOffset timestamp,
        string fingerprint,
        string level = "error",
        string? environment = "production") =>
        TestData.CreatePayloadEnvelope(
            Guid.NewGuid(),
            projectId,
            timestamp,
            TestData.CreateExceptionPayload(
                level: level,
                environment: environment,
                timestamp: timestamp,
                fingerprint: [fingerprint]));

    private static async Task<int> StoreAsync(HttpClient client, Envelope envelope)
    {
        using var response = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestData.ReadStoredAsync(response);
    }
}
