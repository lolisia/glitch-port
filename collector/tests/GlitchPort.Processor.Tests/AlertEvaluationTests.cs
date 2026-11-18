using System.Text.Json;
using GlitchPort.Processor.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// Behaviour of <see cref="AlertEvaluator"/>: the frequency threshold and
/// window, level and environment matching, the cooldown dedupe bucket, the
/// evaluation advisory lock and the expired-notification cleanup.
/// </summary>
[Collection(ProcessorCollection.Name)]
public sealed class AlertEvaluationTests
{
    private readonly ProcessorFixture _fixture;
    private readonly NpgsqlDataSource _dataSource;

    public AlertEvaluationTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
        _dataSource = fixture.Factory.Services.GetRequiredService<NpgsqlDataSource>();
    }

    [Fact]
    public async Task Threshold_window_level_and_environment_control_queueing()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            var ruleId = await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                name: "frequent errors",
                trigger: "event_frequency",
                minLevel: "error",
                environment: "production",
                threshold: 3,
                windowMinutes: 60);

            var issueId = await AlertTestSupport.InsertIssueAsync(
                _fixture.ConnectionString,
                projectId,
                "fingerprint-frequency");

            await AlertTestSupport.InsertEventAsync(_fixture.ConnectionString, projectId, issueId);
            await AlertTestSupport.InsertEventAsync(_fixture.ConnectionString, projectId, issueId);

            var evaluator = CreateEvaluator();

            // Two matching events are below the threshold of three.
            Assert.NotNull(await evaluator.EvaluateAsync(CancellationToken.None));
            Assert.Equal(0, await CountNotificationsAsync(projectId));

            // An old event, an event below min_level and an event in another
            // environment do not count.
            await AlertTestSupport.InsertEventAsync(
                _fixture.ConnectionString,
                projectId,
                issueId,
                secondsAgo: 7_200);
            await AlertTestSupport.InsertEventAsync(
                _fixture.ConnectionString,
                projectId,
                issueId,
                level: "info");
            await AlertTestSupport.InsertEventAsync(
                _fixture.ConnectionString,
                projectId,
                issueId,
                environment: "staging");

            Assert.NotNull(await evaluator.EvaluateAsync(CancellationToken.None));
            Assert.Equal(0, await CountNotificationsAsync(projectId));

            // The third matching event inside the window reaches the threshold.
            await AlertTestSupport.InsertEventAsync(_fixture.ConnectionString, projectId, issueId);

            var result = await evaluator.EvaluateAsync(CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(1, result!.Queued);

            var notification = Assert.Single(
                await AlertTestSupport.GetNotificationsAsync(_fixture.ConnectionString, projectId));
            Assert.StartsWith($"frequency:{ruleId}:{issueId}:", notification.DedupeKey);
            Assert.Equal("event_frequency", notification.Trigger);
            Assert.Equal("pending", notification.Status);

            using var payload = JsonDocument.Parse(notification.PayloadJson);
            Assert.Equal(
                "event_frequency",
                payload.RootElement.GetProperty("trigger").GetString());
            Assert.Equal(
                projectId,
                payload.RootElement.GetProperty("project").GetProperty("id").GetInt64());
            Assert.Equal(
                issueId,
                payload.RootElement.GetProperty("issue").GetProperty("id").GetInt64());
            Assert.Equal(
                ruleId,
                payload.RootElement.GetProperty("rule").GetProperty("id").GetInt64());
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task An_issue_is_queued_once_per_cooldown_bucket()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                trigger: "event_frequency",
                threshold: 1,
                windowMinutes: 60,
                cooldownMinutes: 10_080);

            var issueId = await AlertTestSupport.InsertIssueAsync(
                _fixture.ConnectionString,
                projectId,
                "fingerprint-cooldown");
            await AlertTestSupport.InsertEventAsync(_fixture.ConnectionString, projectId, issueId);

            var evaluator = CreateEvaluator();

            var first = await evaluator.EvaluateAsync(CancellationToken.None);
            Assert.Equal(1, first!.Queued);

            var second = await evaluator.EvaluateAsync(CancellationToken.None);
            Assert.Equal(0, second!.Queued);

            Assert.Equal(1, await CountNotificationsAsync(projectId));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Only_the_instance_holding_the_advisory_lock_evaluates()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                trigger: "event_frequency",
                threshold: 1,
                windowMinutes: 60);

            var issueId = await AlertTestSupport.InsertIssueAsync(
                _fixture.ConnectionString,
                projectId,
                "fingerprint-lock");
            await AlertTestSupport.InsertEventAsync(_fixture.ConnectionString, projectId, issueId);

            // Another session (the first evaluator) holds the lock, so neither
            // of the two concurrent evaluators may run.
            await using var lockConnection = new NpgsqlConnection(UnpooledConnectionString());
            await lockConnection.OpenAsync();
            await using var acquire = new NpgsqlCommand(
                $"select pg_advisory_lock({AlertEvaluator.AdvisoryLockKey})",
                lockConnection);
            await acquire.ExecuteScalarAsync();

            try
            {
                var blocked = await Task.WhenAll(
                    CreateEvaluator().EvaluateAsync(CancellationToken.None),
                    CreateEvaluator().EvaluateAsync(CancellationToken.None));

                Assert.All(blocked, Assert.Null);
                Assert.Equal(0, await CountNotificationsAsync(projectId));
            }
            finally
            {
                await using var release = new NpgsqlCommand(
                    $"select pg_advisory_unlock({AlertEvaluator.AdvisoryLockKey})",
                    lockConnection);
                await release.ExecuteScalarAsync();
            }

            // Once the lock is free exactly one evaluation queues the match.
            var result = await CreateEvaluator().EvaluateAsync(CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(1, result!.Queued);
            Assert.Equal(1, await CountNotificationsAsync(projectId));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Two_evaluators_queue_at_most_one_notification_per_bucket()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                trigger: "event_frequency",
                threshold: 1,
                windowMinutes: 60,
                cooldownMinutes: 10_080);

            var issueId = await AlertTestSupport.InsertIssueAsync(
                _fixture.ConnectionString,
                projectId,
                "fingerprint-concurrent");
            await AlertTestSupport.InsertEventAsync(_fixture.ConnectionString, projectId, issueId);
            await AlertTestSupport.InsertEventAsync(_fixture.ConnectionString, projectId, issueId);

            var results = await Task.WhenAll(
                CreateEvaluator().EvaluateAsync(CancellationToken.None),
                CreateEvaluator().EvaluateAsync(CancellationToken.None));

            Assert.Contains(results, static result => result is not null);
            Assert.Equal(1, await CountNotificationsAsync(projectId));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Evaluation_deletes_expired_sent_and_failed_notifications()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            var ruleId = await AlertTestSupport.InsertRuleAsync(_fixture.ConnectionString, projectId);
            var old = DateTimeOffset.UtcNow.AddDays(-40);

            var expiredSent = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload(),
                status: "sent",
                createdAt: old);
            var expiredFailed = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload(),
                status: "failed",
                createdAt: old);
            var expiredPending = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload(),
                status: "pending",
                createdAt: old);
            var recentSent = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload(),
                status: "sent",
                createdAt: DateTimeOffset.UtcNow.AddDays(-1));

            var result = await CreateEvaluator(new AlertOptions { RetentionDays = 30 })
                .EvaluateAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(2, result!.Deleted);
            Assert.Equal(0, await ExistsAsync(expiredSent));
            Assert.Equal(0, await ExistsAsync(expiredFailed));
            Assert.Equal(1, await ExistsAsync(expiredPending));
            Assert.Equal(1, await ExistsAsync(recentSent));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    private AlertEvaluator CreateEvaluator(AlertOptions? options = null) =>
        new(
            _dataSource,
            Options.Create(options ?? new AlertOptions()),
            NullLogger<AlertEvaluator>.Instance);

    private Task<long> CountNotificationsAsync(long projectId) =>
        AlertTestSupport.CountNotificationsAsync(_fixture.ConnectionString, projectId);

    private Task<long> ExistsAsync(long notificationId) =>
        TestDatabase.QuerySingleLongAsync(
            _fixture.ConnectionString,
            $"select count(*) from alert_notifications where id = {notificationId}");

    private string UnpooledConnectionString() =>
        new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
}
