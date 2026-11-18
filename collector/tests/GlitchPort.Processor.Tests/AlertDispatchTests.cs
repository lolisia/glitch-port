using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using GlitchPort.Processor.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// Behaviour of <see cref="AlertDispatcher"/>: claiming with
/// <c>for update skip locked</c>, success, retry and failed transitions,
/// <c>Retry-After</c>, error truncation and the guarantee that two dispatchers
/// never send the same notification twice.
/// </summary>
[Collection(ProcessorCollection.Name)]
public sealed class AlertDispatchTests
{
    private readonly ProcessorFixture _fixture;
    private readonly NpgsqlDataSource _dataSource;

    public AlertDispatchTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
        _dataSource = fixture.Factory.Services.GetRequiredService<NpgsqlDataSource>();
    }

    [Fact]
    public async Task Successful_delivery_marks_the_notification_sent()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            // A dispatch run is global; start from a queue that only contains
            // this test's rows so the claim counters are exact.
            await AlertTestSupport.ResetNotificationsAsync(_fixture.ConnectionString);

            var ruleId = await AlertTestSupport.InsertRuleAsync(
                _fixture.ConnectionString,
                projectId,
                channel: "webhook");
            var notificationId = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload(issueId: 41));

            var handler = new RecordingHttpMessageHandler(_ => AlertTestSupport.Ok());
            var result = await CreateDispatcher(handler).DispatchAsync(CancellationToken.None);

            Assert.Equal(1, result.Claimed);
            Assert.Equal(1, result.Delivered);
            Assert.Equal(0, result.Retried);
            Assert.Equal(0, result.Failed);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(AlertTestSupport.TargetUrl, request.Url);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("new_issue", body.RootElement.GetProperty("trigger").GetString());
            Assert.Equal(41, body.RootElement.GetProperty("issue").GetProperty("id").GetInt64());

            var notification = await AlertTestSupport.GetNotificationAsync(
                _fixture.ConnectionString,
                notificationId);
            Assert.Equal("sent", notification.Status);
            Assert.Equal(1, notification.Attempts);
            Assert.Null(notification.LastError);
            Assert.Null(notification.LockedUntil);
            Assert.NotNull(notification.SentAt);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Failed_deliveries_retry_with_backoff_and_end_as_failed()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            await AlertTestSupport.ResetNotificationsAsync(_fixture.ConnectionString);

            var ruleId = await AlertTestSupport.InsertRuleAsync(_fixture.ConnectionString, projectId);
            var notificationId = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload());

            var handler = new RecordingHttpMessageHandler(_ => AlertTestSupport.Status(HttpStatusCode.InternalServerError));
            var dispatcher = CreateDispatcher(handler, maxAttempts: 3);

            var first = await dispatcher.DispatchAsync(CancellationToken.None);
            Assert.Equal(1, first.Retried);

            var afterFirst = await AlertTestSupport.GetNotificationAsync(
                _fixture.ConnectionString,
                notificationId);
            Assert.Equal("pending", afterFirst.Status);
            Assert.Equal(1, afterFirst.Attempts);
            Assert.Equal("500", afterFirst.LastError);
            AlertTestSupport.AssertDelay(afterFirst.NextAttemptAt, TimeSpan.FromSeconds(60));

            await AlertTestSupport.MakeDueAsync(_fixture.ConnectionString, notificationId);
            var second = await dispatcher.DispatchAsync(CancellationToken.None);
            Assert.Equal(1, second.Retried);

            var afterSecond = await AlertTestSupport.GetNotificationAsync(
                _fixture.ConnectionString,
                notificationId);
            Assert.Equal("pending", afterSecond.Status);
            Assert.Equal(2, afterSecond.Attempts);
            AlertTestSupport.AssertDelay(afterSecond.NextAttemptAt, TimeSpan.FromSeconds(120));

            // The third attempt reaches MaxAttempts and is not retried again.
            await AlertTestSupport.MakeDueAsync(_fixture.ConnectionString, notificationId);
            var third = await dispatcher.DispatchAsync(CancellationToken.None);
            Assert.Equal(1, third.Failed);

            var afterThird = await AlertTestSupport.GetNotificationAsync(
                _fixture.ConnectionString,
                notificationId);
            Assert.Equal("failed", afterThird.Status);
            Assert.Equal(3, afterThird.Attempts);
            Assert.Equal("500", afterThird.LastError);
            Assert.Null(afterThird.SentAt);

            var fourth = await dispatcher.DispatchAsync(CancellationToken.None);
            Assert.Equal(0, fourth.Claimed);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Delivery_exceptions_are_stored_as_a_500_character_error()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            await AlertTestSupport.ResetNotificationsAsync(_fixture.ConnectionString);

            var ruleId = await AlertTestSupport.InsertRuleAsync(_fixture.ConnectionString, projectId);
            var notificationId = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload());

            var handler = new RecordingHttpMessageHandler(_ =>
                throw new HttpRequestException(new string('x', 600)));
            var result = await CreateDispatcher(handler).DispatchAsync(CancellationToken.None);

            Assert.Equal(1, result.Retried);
            var notification = await AlertTestSupport.GetNotificationAsync(
                _fixture.ConnectionString,
                notificationId);
            Assert.Equal(500, notification.LastError!.Length);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Too_many_requests_honours_the_retry_after_header()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            await AlertTestSupport.ResetNotificationsAsync(_fixture.ConnectionString);

            var ruleId = await AlertTestSupport.InsertRuleAsync(_fixture.ConnectionString, projectId);
            var notificationId = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload());

            var handler = new RecordingHttpMessageHandler(_ =>
            {
                var response = AlertTestSupport.Status(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(120));
                return response;
            });

            var result = await CreateDispatcher(handler).DispatchAsync(CancellationToken.None);
            Assert.Equal(1, result.Retried);

            var notification = await AlertTestSupport.GetNotificationAsync(
                _fixture.ConnectionString,
                notificationId);
            Assert.Equal("pending", notification.Status);
            Assert.Equal("429", notification.LastError);
            AlertTestSupport.AssertDelay(notification.NextAttemptAt, TimeSpan.FromSeconds(120));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Too_many_requests_honours_a_retry_after_http_date()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            await AlertTestSupport.ResetNotificationsAsync(_fixture.ConnectionString);

            var ruleId = await AlertTestSupport.InsertRuleAsync(_fixture.ConnectionString, projectId);
            var notificationId = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload());

            var handler = new RecordingHttpMessageHandler(_ =>
            {
                var response = AlertTestSupport.Status(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    DateTimeOffset.UtcNow.AddSeconds(90));
                return response;
            });

            await CreateDispatcher(handler).DispatchAsync(CancellationToken.None);

            var notification = await AlertTestSupport.GetNotificationAsync(
                _fixture.ConnectionString,
                notificationId);
            Assert.Equal("pending", notification.Status);
            AlertTestSupport.AssertDelay(notification.NextAttemptAt, TimeSpan.FromSeconds(90));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Only_due_and_unlocked_notifications_are_claimed()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            await AlertTestSupport.ResetNotificationsAsync(_fixture.ConnectionString);

            var ruleId = await AlertTestSupport.InsertRuleAsync(_fixture.ConnectionString, projectId);
            var due = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload(issueId: 1));
            var future = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload(issueId: 2),
                nextAttemptAt: DateTimeOffset.UtcNow.AddHours(1));
            var locked = await AlertTestSupport.InsertNotificationAsync(
                _fixture.ConnectionString,
                ruleId,
                projectId,
                AlertTestSupport.CreatePayload(issueId: 3),
                status: "sending",
                attempts: 1,
                lockedUntil: DateTimeOffset.UtcNow.AddMinutes(5));

            var handler = new RecordingHttpMessageHandler(_ => AlertTestSupport.Ok());
            var result = await CreateDispatcher(handler).DispatchAsync(CancellationToken.None);

            Assert.Equal(1, result.Claimed);
            Assert.Single(handler.Requests);

            Assert.Equal(
                "sent",
                (await AlertTestSupport.GetNotificationAsync(_fixture.ConnectionString, due)).Status);
            Assert.Equal(
                "pending",
                (await AlertTestSupport.GetNotificationAsync(_fixture.ConnectionString, future)).Status);
            Assert.Equal(
                "sending",
                (await AlertTestSupport.GetNotificationAsync(_fixture.ConnectionString, locked)).Status);
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    [Fact]
    public async Task Two_dispatchers_claim_every_notification_exactly_once()
    {
        var projectId = TestData.NextProjectId();
        try
        {
            // Other tests may have left notifications behind; a dispatch run is
            // global, so start from a queue that only contains this test's rows.
            await AlertTestSupport.ResetNotificationsAsync(_fixture.ConnectionString);

            var ruleId = await AlertTestSupport.InsertRuleAsync(_fixture.ConnectionString, projectId);
            for (var index = 1; index <= 25; index++)
            {
                await AlertTestSupport.InsertNotificationAsync(
                    _fixture.ConnectionString,
                    ruleId,
                    projectId,
                    AlertTestSupport.CreatePayload(issueId: index),
                    dedupeKey: Guid.NewGuid().ToString("N"));
            }

            var handler = new RecordingHttpMessageHandler(_ => AlertTestSupport.Ok());
            var first = CreateDispatcher(handler);
            var second = CreateDispatcher(handler);

            var results = await Task.WhenAll(
                first.DispatchAsync(CancellationToken.None),
                second.DispatchAsync(CancellationToken.None));

            Assert.Equal(25, results.Sum(static result => result.Claimed));
            Assert.Equal(25, results.Sum(static result => result.Delivered));
            Assert.Equal(25, handler.Requests.Count);

            // A row that was claimed twice would produce a duplicate body.
            var bodies = handler.Requests
                .Select(static request => request.Body)
                .Distinct(StringComparer.Ordinal)
                .Count();
            Assert.Equal(25, bodies);

            Assert.Equal(
                25,
                await AlertTestSupport.CountSentAsync(_fixture.ConnectionString, ruleId));
            Assert.Equal(
                0,
                await AlertTestSupport.CountWithAttemptsAboveOneAsync(_fixture.ConnectionString, ruleId));
        }
        finally
        {
            await AlertTestSupport.CleanupProjectAsync(_fixture.ConnectionString, projectId);
        }
    }

    private AlertDispatcher CreateDispatcher(
        HttpMessageHandler handler,
        int maxAttempts = 5) =>
        new(
            _dataSource,
            new FakeHttpClientFactory(handler),
            Options.Create(new AlertOptions
            {
                Enabled = true,
                MaxAttempts = maxAttempts,
            }),
            NullLogger<AlertDispatcher>.Instance);
}

/// <summary>A request recorded by <see cref="RecordingHttpMessageHandler"/>.</summary>
internal sealed record RecordedAlertRequest(string Url, string Body);

/// <summary>
/// Captures alert requests and answers them with a configurable response.
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedAlertRequest> _requests = new();
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        _respond = respond ?? (_ => AlertTestSupport.Ok());
    }

    public IReadOnlyList<RecordedAlertRequest> Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        _requests.Enqueue(new RecordedAlertRequest(request.RequestUri!.ToString(), body));

        return _respond(request);
    }
}

/// <summary>Creates clients backed by a test handler.</summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
}

/// <summary>A notification row read for assertions.</summary>
internal sealed record AlertNotificationRow(
    long Id,
    long RuleId,
    long? IssueId,
    string DedupeKey,
    string Trigger,
    string PayloadJson,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? LockedUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);

/// <summary>
/// Database helpers shared by the alert tests. They live here so every alert
/// test file can use them without adding another support file.
/// </summary>
internal static class AlertTestSupport
{
    /// <summary>Delivery target used by the rules the tests create.</summary>
    public const string TargetUrl = "https://alerts.example.com/hook";

    /// <summary>Builds a payload with predictable values for assertions.</summary>
    public static AlertPayload CreatePayload(
        string trigger = "new_issue",
        string level = "error",
        long issueId = 42,
        string? environment = "production",
        string? url = "https://dashboard.example.com#/issues/42") =>
        new(
            trigger,
            new AlertProject(7, "Star Raiders", "star-raiders"),
            new AlertIssue(
                issueId,
                "NullReferenceException: Object reference not set",
                "Weapon.Fire",
                level,
                "unresolved",
                12,
                new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.Zero),
                "star-raiders@1.0.0",
                "star-raiders@1.2.3",
                environment),
            new AlertRuleRef(3, "Notify on new issues"),
            url);

    /// <summary>A 200 response.</summary>
    public static HttpResponseMessage Ok() => Status(HttpStatusCode.OK);

    /// <summary>A response with <paramref name="statusCode"/>.</summary>
    public static HttpResponseMessage Status(HttpStatusCode statusCode) =>
        new(statusCode);

    /// <summary>
    /// Asserts that <paramref name="nextAttemptAt"/> is about
    /// <paramref name="delay"/> from now (the dispatcher computes the delay
    /// with the same clock the test reads).
    /// </summary>
    public static void AssertDelay(DateTimeOffset nextAttemptAt, TimeSpan delay)
    {
        var actual = nextAttemptAt - DateTimeOffset.UtcNow;
        var tolerance = TimeSpan.FromSeconds(20);
        Assert.True(
            actual >= delay - tolerance && actual <= delay + tolerance,
            $"Expected the next attempt in about {delay}, but it is scheduled in {actual}.");
    }

    /// <summary>Inserts an enabled rule and returns its id.</summary>
    public static Task<long> InsertRuleAsync(
        string connectionString,
        long projectId,
        string name = "alert rule",
        string trigger = "new_issue",
        string? minLevel = null,
        string? environment = null,
        int? threshold = null,
        int? windowMinutes = null,
        int cooldownMinutes = 60,
        string channel = "webhook",
        string targetUrl = TargetUrl,
        bool isEnabled = true) =>
        TestDatabase.QuerySingleLongAsync(
            connectionString,
            $"""
            insert into alert_rules (
                project_id,
                name,
                trigger,
                min_level,
                environment,
                threshold,
                window_minutes,
                cooldown_minutes,
                channel,
                target_url,
                is_enabled)
            values (
                {projectId},
                {Quote(name)},
                {Quote(trigger)},
                {NullableString(minLevel)},
                {NullableString(environment)},
                {NullableNumber(threshold)},
                {NullableNumber(windowMinutes)},
                {cooldownMinutes},
                {Quote(channel)},
                {Quote(targetUrl)},
                {isEnabled.ToString().ToLowerInvariant()})
            returning id
            """);

    /// <summary>Inserts a pending notification and returns its id.</summary>
    public static Task<long> InsertNotificationAsync(
        string connectionString,
        long ruleId,
        long projectId,
        AlertPayload payload,
        string? dedupeKey = null,
        string status = "pending",
        int attempts = 0,
        DateTimeOffset? nextAttemptAt = null,
        DateTimeOffset? lockedUntil = null,
        string? lastError = null,
        DateTimeOffset? createdAt = null) =>
        TestDatabase.QuerySingleLongAsync(
            connectionString,
            $"""
            insert into alert_notifications (
                rule_id,
                project_id,
                issue_id,
                trigger,
                dedupe_key,
                payload,
                status,
                attempts,
                next_attempt_at,
                locked_until,
                last_error,
                created_at)
            values (
                {ruleId},
                {projectId},
                null,
                {Quote(payload.Trigger)},
                {Quote(dedupeKey ?? Guid.NewGuid().ToString("N"))},
                {Quote(AlertPayloads.Serialize(payload))}::jsonb,
                {Quote(status)},
                {attempts},
                {(nextAttemptAt is null ? "now()" : Timestamp(nextAttemptAt.Value))},
                {NullableTimestamp(lockedUntil)},
                {NullableString(lastError)},
                {(createdAt is null ? "now()" : Timestamp(createdAt.Value))})
            returning id
            """);

    /// <summary>Inserts an issue and returns its id.</summary>
    public static Task<long> InsertIssueAsync(
        string connectionString,
        long projectId,
        string fingerprint,
        string level = "error",
        long eventCount = 0,
        string? environment = "production") =>
        TestDatabase.QuerySingleLongAsync(
            connectionString,
            $"""
            insert into issues (
                project_id,
                fingerprint,
                title,
                level,
                platform,
                first_seen,
                last_seen,
                event_count,
                last_environment)
            values (
                {projectId},
                {Quote(fingerprint)},
                {Quote(fingerprint)},
                {Quote(level)},
                'unity',
                now() - interval '10 minutes',
                now(),
                {eventCount},
                {NullableString(environment)})
            returning id
            """);

    /// <summary>Inserts one event for an issue.</summary>
    public static Task InsertEventAsync(
        string connectionString,
        long projectId,
        long issueId,
        string level = "error",
        string? environment = "production",
        int secondsAgo = 0) =>
        TestDatabase.ExecuteAsync(
            connectionString,
            $$"""
            insert into events (
                id,
                project_id,
                issue_id,
                timestamp,
                received_at,
                level,
                platform,
                environment,
                endpoint,
                payload)
            values (
                '{{Guid.NewGuid()}}',
                {{projectId}},
                {{issueId}},
                now() - interval '{{secondsAgo}} seconds',
                now() - interval '{{secondsAgo}} seconds',
                {{Quote(level)}},
                'unity',
                {{NullableString(environment)}},
                'POST /api/1/events',
                '{}'::jsonb)
            """);

    /// <summary>Reads one notification row.</summary>
    public static async Task<AlertNotificationRow> GetNotificationAsync(
        string connectionString,
        long id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select id,
                   rule_id,
                   issue_id,
                   dedupe_key,
                   trigger,
                   payload::text,
                   status,
                   attempts,
                   last_error,
                   next_attempt_at,
                   locked_until,
                   created_at,
                   sent_at
            from alert_notifications
            where id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"Notification {id} was not found.");

        return new AlertNotificationRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            AsUtc(reader.GetDateTime(9)),
            reader.IsDBNull(10) ? null : AsUtc(reader.GetDateTime(10)),
            AsUtc(reader.GetDateTime(11)),
            reader.IsDBNull(12) ? null : AsUtc(reader.GetDateTime(12)));
    }

    /// <summary>Reads every notification of a project ordered by id.</summary>
    public static async Task<List<AlertNotificationRow>> GetNotificationsAsync(
        string connectionString,
        long projectId)
    {
        var ids = new List<long>();

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "select id from alert_notifications where project_id = @project_id order by id",
                connection);
            command.Parameters.AddWithValue("project_id", projectId);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        var rows = new List<AlertNotificationRow>(ids.Count);
        foreach (var id in ids)
        {
            rows.Add(await GetNotificationAsync(connectionString, id));
        }

        return rows;
    }

    /// <summary>Counts the notifications of a project.</summary>
    public static Task<long> CountNotificationsAsync(string connectionString, long projectId) =>
        TestDatabase.QuerySingleLongAsync(
            connectionString,
            $"select count(*) from alert_notifications where project_id = {projectId}");

    /// <summary>Counts the sent notifications of a rule.</summary>
    public static Task<long> CountSentAsync(string connectionString, long ruleId) =>
        TestDatabase.QuerySingleLongAsync(
            connectionString,
            $"select count(*) from alert_notifications where rule_id = {ruleId} and status = 'sent'");

    /// <summary>Counts the notifications of a rule that were claimed more than once.</summary>
    public static Task<long> CountWithAttemptsAboveOneAsync(string connectionString, long ruleId) =>
        TestDatabase.QuerySingleLongAsync(
            connectionString,
            $"select count(*) from alert_notifications where rule_id = {ruleId} and attempts > 1");

    /// <summary>Makes a notification due immediately.</summary>
    public static Task MakeDueAsync(string connectionString, long id) =>
        TestDatabase.ExecuteAsync(
            connectionString,
            $"update alert_notifications set next_attempt_at = now() - interval '1 second' where id = {id}");

    /// <summary>Deletes every notification, so a global dispatch run is isolated.</summary>
    public static Task ResetNotificationsAsync(string connectionString) =>
        TestDatabase.ExecuteAsync(connectionString, "delete from alert_notifications");

    /// <summary>
    /// Deletes a project, cascading to its issues, events, rules and
    /// notifications.
    /// </summary>
    public static Task CleanupProjectAsync(string connectionString, long projectId) =>
        TestDatabase.ExecuteAsync(connectionString, $"delete from projects where id = {projectId}");

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string NullableString(string? value) => value is null ? "null" : Quote(value);

    private static string NullableNumber(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string Timestamp(DateTimeOffset value) =>
        $"'{value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}'";

    private static string NullableTimestamp(DateTimeOffset? value) =>
        value is null ? "null" : Timestamp(value.Value);
}
