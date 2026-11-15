using System.Globalization;
using System.Net;
using System.Text.Json;
using GlitchPort.Processor.Migrations;
using GlitchPort.Processor.Retention;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// End-to-end behaviour of <see cref="RetentionRunner"/>: the advisory lock,
/// batched deletion, issue counter maintenance, the maintenance_runs record and
/// the management status endpoint.
/// </summary>
[Collection(ProcessorCollection.Name)]
public sealed class RetentionTests
{
    private const string RetentionStatusPath = "/api/manage/maintenance/retention";

    private readonly ProcessorFixture _fixture;
    private readonly NpgsqlDataSource _dataSource;

    public RetentionTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
        _dataSource = fixture.Factory.Services.GetRequiredService<NpgsqlDataSource>();
    }

    [Fact]
    public async Task Expired_events_and_messages_are_deleted_while_newer_rows_survive()
    {
        var projectId = TestData.NextProjectId();
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-40);
        var recent = now.AddMinutes(-5);

        using var client = _fixture.CreateClient();
        var oldEventId = await IngestEventAsync(client, projectId, old, "retention-expired-event");
        var recentEventId = await IngestEventAsync(client, projectId, recent, "retention-recent-event");

        await InsertMessageAsync(projectId, old, "retention-expired-message");
        await InsertMessageAsync(projectId, recent, "retention-recent-message");

        var lastRunId = await QuerySingleLongAsync("select coalesce(max(id), 0) from maintenance_runs");

        var result = await CreateRunner(days: 30, batchSize: 10_000).RunAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(30, result.Days);
        Assert.True(result.DeletedEvents >= 1);
        Assert.True(result.DeletedMessages >= 1);

        Assert.Equal(0, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, oldEventId));
        Assert.Equal(1, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, recentEventId));
        Assert.Equal(0, await CountMessagesAsync(projectId, "retention-expired-message"));
        Assert.Equal(1, await CountMessagesAsync(projectId, "retention-recent-message"));

        // The run released its advisory lock, so another session can take it.
        Assert.True(await TryAcquireAdvisoryLockAsync());

        // Exactly one maintenance_runs row describes the run.
        var run = await GetSingleRunAfterAsync(lastRunId);
        Assert.Equal(30, run.Days);
        Assert.Equal(result.DeletedEvents, run.DeletedEvents);
        Assert.Equal(result.DeletedIssues, run.DeletedIssues);
        Assert.Equal(result.DeletedMessages, run.DeletedMessages);
    }

    [Fact]
    public async Task Batches_smaller_than_the_expired_row_count_delete_every_expired_row()
    {
        var projectId = TestData.NextProjectId();
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-40);

        using var client = _fixture.CreateClient();
        var eventIds = new List<Guid>();
        for (var index = 0; index < 7; index++)
        {
            eventIds.Add(await IngestEventAsync(
                client,
                projectId,
                old.AddMinutes(index),
                $"retention-batch-{index}"));
        }

        await InsertMessageAsync(projectId, old, "retention-batch-message-1");
        await InsertMessageAsync(projectId, old.AddMinutes(1), "retention-batch-message-2");
        await InsertMessageAsync(projectId, old.AddMinutes(2), "retention-batch-message-3");

        var result = await CreateRunner(days: 30, batchSize: 2).RunAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.DeletedEvents >= 7);
        Assert.True(result.DeletedMessages >= 3);

        foreach (var eventId in eventIds)
        {
            Assert.Equal(0, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, eventId));
        }

        Assert.Equal(0, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, projectId));

        // Every issue of the project lost all of its events and was expired,
        // so the issues are gone as well.
        Assert.Equal(0, await TestDatabase.CountIssuesAsync(_fixture.ConnectionString, projectId));

        Assert.Equal(0, await CountMessagesAsync(projectId, "retention-batch-message-1"));
        Assert.Equal(0, await CountMessagesAsync(projectId, "retention-batch-message-2"));
        Assert.Equal(0, await CountMessagesAsync(projectId, "retention-batch-message-3"));
    }

    [Fact]
    public async Task Issue_counts_are_recomputed_and_empty_issues_follow_the_cutoff()
    {
        var survivingProject = TestData.NextProjectId();
        var expiredProject = TestData.NextProjectId();
        var recentEmptyProject = TestData.NextProjectId();
        var now = DateTimeOffset.UtcNow;

        using var client = _fixture.CreateClient();

        // One issue with three events, two of which are older than the cutoff.
        const string SharedFingerprint = "retention-shared-fingerprint";
        await IngestEventAsync(client, survivingProject, now.AddDays(-40), SharedFingerprint);
        await IngestEventAsync(client, survivingProject, now.AddDays(-39), SharedFingerprint);
        await IngestEventAsync(client, survivingProject, now.AddHours(-1), SharedFingerprint);

        // One issue whose only event is expired; it must disappear entirely.
        await IngestEventAsync(client, expiredProject, now.AddDays(-40), "retention-only-expired");

        // An issue without events that was seen recently must survive with a
        // zero counter.
        await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            recentEmptyProject,
            "retention-recent-empty",
            now.AddDays(-2),
            now.AddHours(-1),
            eventCount: 0);

        var before = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, survivingProject);
        Assert.Equal(3, before.EventCount);

        var result = await CreateRunner(days: 30, batchSize: 10_000).RunAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.DeletedIssues >= 1);

        var survivor = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, survivingProject);
        Assert.Equal(1, survivor.EventCount);
        Assert.Equal(1, await TestDatabase.CountIssuesAsync(_fixture.ConnectionString, survivingProject));

        Assert.Equal(0, await TestDatabase.CountIssuesAsync(_fixture.ConnectionString, expiredProject));

        var recentEmpty = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, recentEmptyProject);
        Assert.Equal(0, recentEmpty.EventCount);
        Assert.Equal(1, await TestDatabase.CountIssuesAsync(_fixture.ConnectionString, recentEmptyProject));
    }

    [Fact]
    public async Task Days_zero_disables_every_deletion()
    {
        var projectId = TestData.NextProjectId();
        var old = DateTimeOffset.UtcNow.AddDays(-40);

        using var client = _fixture.CreateClient();
        var eventId = await IngestEventAsync(client, projectId, old, "retention-disabled-event");
        await InsertMessageAsync(projectId, old, "retention-disabled-message");

        var runsBefore = await CountRetentionRunsAsync();

        var result = await CreateRunner(days: 0, batchSize: 100).RunAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, eventId));
        Assert.Equal(1, await CountMessagesAsync(projectId, "retention-disabled-message"));
        Assert.Equal(runsBefore, await CountRetentionRunsAsync());
    }

    [Fact]
    public async Task A_run_returns_null_when_another_session_holds_the_advisory_lock()
    {
        Assert.Equal(823652, RetentionRunner.AdvisoryLockKey);
        Assert.NotEqual(DatabaseMigrator.AdvisoryLockKey, RetentionRunner.AdvisoryLockKey);

        var projectId = TestData.NextProjectId();
        var old = DateTimeOffset.UtcNow.AddDays(-40);

        using var client = _fixture.CreateClient();
        var eventId = await IngestEventAsync(client, projectId, old, "retention-locked-event");
        await InsertMessageAsync(projectId, old, "retention-locked-message");

        var runsBefore = await CountRetentionRunsAsync();

        await using var lockConnection = new NpgsqlConnection(UnpooledConnectionString());
        await lockConnection.OpenAsync();
        await using (var lockCommand = new NpgsqlCommand(
                         $"select pg_advisory_lock({RetentionRunner.AdvisoryLockKey})",
                         lockConnection))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        try
        {
            var result = await CreateRunner(days: 30, batchSize: 100).RunAsync(CancellationToken.None);

            Assert.Null(result);
            Assert.Equal(1, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, eventId));
            Assert.Equal(1, await CountMessagesAsync(projectId, "retention-locked-message"));
            Assert.Equal(runsBefore, await CountRetentionRunsAsync());
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand(
                $"select pg_advisory_unlock({RetentionRunner.AdvisoryLockKey})",
                lockConnection);
            await unlockCommand.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Concurrent_runners_execute_one_run_and_never_delete_twice()
    {
        var projectId = TestData.NextProjectId();
        var old = DateTimeOffset.UtcNow.AddDays(-40);

        using var client = _fixture.CreateClient();
        var eventIds = new List<Guid>();
        for (var index = 0; index < 30; index++)
        {
            eventIds.Add(await IngestEventAsync(
                client,
                projectId,
                old.AddSeconds(index),
                $"retention-concurrent-{index}"));
        }

        var runsBefore = await CountRetentionRunsAsync();

        await using var firstSource = CreateIsolatedDataSource();
        await using var secondSource = CreateIsolatedDataSource();

        var firstRunner = CreateRunner(firstSource, days: 30, batchSize: 1);
        var secondRunner = CreateRunner(secondSource, days: 30, batchSize: 1);

        // Start the first run and wait until it really holds the lock, so the
        // second runner provably races against a running run instead of a
        // finished one.
        var firstRun = firstRunner.RunAsync(CancellationToken.None);
        await WaitForAdvisoryLockAsync();

        var secondResult = await secondRunner.RunAsync(CancellationToken.None);
        var firstResult = await firstRun;

        Assert.Null(secondResult);
        Assert.NotNull(firstResult);
        Assert.True(firstResult.DeletedEvents >= eventIds.Count);

        foreach (var eventId in eventIds)
        {
            Assert.Equal(0, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, eventId));
        }

        // Only the runner that acquired the lock wrote a maintenance_runs row.
        Assert.Equal(runsBefore + 1, await CountRetentionRunsAsync());
    }

    [Fact]
    public async Task Runs_log_structured_start_and_completion_counters()
    {
        var projectId = TestData.NextProjectId();
        var old = DateTimeOffset.UtcNow.AddDays(-40);

        using var client = _fixture.CreateClient();
        await IngestEventAsync(client, projectId, old, "retention-logged-event");

        var logger = new RecordingLogger<RetentionRunner>();
        var result = await CreateRunner(days: 30, batchSize: 100, logger: logger)
            .RunAsync(CancellationToken.None);

        Assert.NotNull(result);

        var started = Assert.Single(logger.Entries, entry => entry.Message.Contains("Retention run started"));
        var finished = Assert.Single(logger.Entries, entry => entry.Message.Contains("Retention run finished"));

        string[] expectedProperties =
            ["Days", "BatchSize", "DeletedEvents", "DeletedIssues", "DeletedMessages", "DurationMs"];

        foreach (var entry in new[] { started, finished })
        {
            var properties = entry.Properties.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var property in expectedProperties)
            {
                Assert.Contains(property, properties);
            }
        }

        Assert.Equal(30, Convert.ToInt32(started.Properties["Days"], CultureInfo.InvariantCulture));
        Assert.Equal(100, Convert.ToInt32(started.Properties["BatchSize"], CultureInfo.InvariantCulture));
        Assert.Equal(
            result.DeletedEvents,
            Convert.ToInt64(finished.Properties["DeletedEvents"], CultureInfo.InvariantCulture));
        Assert.Equal(
            result.DeletedIssues,
            Convert.ToInt64(finished.Properties["DeletedIssues"], CultureInfo.InvariantCulture));
        Assert.Equal(
            result.DeletedMessages,
            Convert.ToInt64(finished.Properties["DeletedMessages"], CultureInfo.InvariantCulture));
        Assert.True(Convert.ToInt64(finished.Properties["DurationMs"], CultureInfo.InvariantCulture) >= 0);
    }

    [Fact]
    public async Task Retention_endpoint_returns_the_configured_days_and_the_last_run()
    {
        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"delete from maintenance_runs where kind = '{RetentionRunner.Kind}'");

        using var api = new ManagementApiClient(_fixture);

        using var emptyResponse = await api.GetAsync(RetentionStatusPath);
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);

        var empty = await ManagementApiData.ReadJsonAsync(emptyResponse);
        Assert.Equal(
            ["days", "last_run_at", "last_deleted_events", "last_deleted_issues"],
            empty.EnumerateObject().Select(property => property.Name));
        Assert.Equal(30, empty.GetProperty("days").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("last_run_at").ValueKind);
        Assert.Equal(0, empty.GetProperty("last_deleted_events").GetInt64());
        Assert.Equal(0, empty.GetProperty("last_deleted_issues").GetInt64());

        var finishedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await InsertMaintenanceRunAsync(30, finishedAt, deletedEvents: 12, deletedIssues: 3, deletedMessages: 5);

        using var response = await api.GetAsync(RetentionStatusPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await ManagementApiData.ReadJsonAsync(response);
        Assert.Equal(30, json.GetProperty("days").GetInt32());
        Assert.Equal(finishedAt, json.GetProperty("last_run_at").GetDateTimeOffset());
        Assert.Equal(12, json.GetProperty("last_deleted_events").GetInt64());
        Assert.Equal(3, json.GetProperty("last_deleted_issues").GetInt64());
    }

    [Fact]
    public async Task Retention_endpoint_requires_the_admin_token()
    {
        using var api = new ManagementApiClient(_fixture);

        using var response = await api.GetAsync(RetentionStatusPath, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private RetentionRunner CreateRunner(
        int days,
        int batchSize,
        ILogger<RetentionRunner>? logger = null) =>
        CreateRunner(_dataSource, days, batchSize, logger);

    private static RetentionRunner CreateRunner(
        NpgsqlDataSource dataSource,
        int days,
        int batchSize,
        ILogger<RetentionRunner>? logger = null) =>
        new(
            dataSource,
            Options.Create(new RetentionOptions { Days = days, BatchSize = batchSize }),
            logger ?? NullLogger<RetentionRunner>.Instance);

    private NpgsqlDataSource CreateIsolatedDataSource() =>
        NpgsqlDataSource.Create(UnpooledConnectionString());

    private string UnpooledConnectionString() =>
        new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

    private async Task<Guid> IngestEventAsync(
        HttpClient client,
        long projectId,
        DateTimeOffset receivedAt,
        string fingerprint) =>
        await ManagementApiData.IngestEventAsync(client, projectId, receivedAt, fingerprint);

    private async Task<Guid> InsertMessageAsync(long projectId, DateTimeOffset receivedAt, string message)
    {
        var id = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into messages (id, project_id, received_at, endpoint, message)
            values (@id, @project_id, @received_at, 'POST /api/1/messages', @message)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("project_id", projectId);
        command.Parameters.AddWithValue("received_at", receivedAt);
        command.Parameters.AddWithValue("message", message);
        await command.ExecuteNonQueryAsync();

        return id;
    }

    private async Task<long> CountMessagesAsync(long projectId, string message)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from messages where project_id = @project_id and message = @message",
            connection);
        command.Parameters.AddWithValue("project_id", projectId);
        command.Parameters.AddWithValue("message", message);

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private Task<long> CountRetentionRunsAsync() =>
        TestDatabase.QuerySingleLongAsync(
            _fixture.ConnectionString,
            $"select count(*) from maintenance_runs where kind = '{RetentionRunner.Kind}'");

    private Task<long> QuerySingleLongAsync(string sql) =>
        TestDatabase.QuerySingleLongAsync(_fixture.ConnectionString, sql);

    private async Task<MaintenanceRunRecord> GetSingleRunAfterAsync(long previousId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select days, started_at, finished_at, deleted_events, deleted_issues, deleted_messages
            from maintenance_runs
            where kind = @kind
              and id > @previous_id
            order by id
            """,
            connection);
        command.Parameters.AddWithValue("kind", RetentionRunner.Kind);
        command.Parameters.AddWithValue("previous_id", previousId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The retention run did not record a maintenance_runs row.");

        var run = new MaintenanceRunRecord(
            reader.GetInt32(0),
            AsUtc(reader.GetFieldValue<DateTime>(1)),
            AsUtc(reader.GetFieldValue<DateTime>(2)),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));

        Assert.False(await reader.ReadAsync(), "The retention run recorded more than one maintenance_runs row.");
        return run;
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private async Task InsertMaintenanceRunAsync(
        int days,
        DateTimeOffset finishedAt,
        long deletedEvents,
        long deletedIssues,
        long deletedMessages)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into maintenance_runs (
                kind, started_at, finished_at, days, deleted_events, deleted_issues, deleted_messages)
            values (
                @kind, @started_at, @finished_at, @days, @deleted_events, @deleted_issues, @deleted_messages)
            """,
            connection);
        command.Parameters.AddWithValue("kind", RetentionRunner.Kind);
        command.Parameters.AddWithValue("started_at", finishedAt.AddMinutes(-1));
        command.Parameters.AddWithValue("finished_at", finishedAt);
        command.Parameters.AddWithValue("days", days);
        command.Parameters.AddWithValue("deleted_events", deletedEvents);
        command.Parameters.AddWithValue("deleted_issues", deletedIssues);
        command.Parameters.AddWithValue("deleted_messages", deletedMessages);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Takes the retention advisory lock from a fresh session and releases it
    /// again, proving that a finished run released its lock.
    /// </summary>
    private async Task<bool> TryAcquireAdvisoryLockAsync()
    {
        await using var connection = new NpgsqlConnection(UnpooledConnectionString());
        await connection.OpenAsync();

        await using var lockCommand = new NpgsqlCommand(
            "select pg_try_advisory_lock(@key)",
            connection);
        lockCommand.Parameters.AddWithValue("key", RetentionRunner.AdvisoryLockKey);

        var acquired = await lockCommand.ExecuteScalarAsync() is true;
        if (!acquired)
        {
            return false;
        }

        await using var unlockCommand = new NpgsqlCommand(
            "select pg_advisory_unlock(@key)",
            connection);
        unlockCommand.Parameters.AddWithValue("key", RetentionRunner.AdvisoryLockKey);
        await unlockCommand.ExecuteNonQueryAsync();

        return true;
    }

    private async Task WaitForAdvisoryLockAsync()
    {
        await using var connection = new NpgsqlConnection(UnpooledConnectionString());
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select count(*)
            from pg_locks
            where locktype = 'advisory'
              and granted
              and (classid::bigint << 32) + objid::bigint = @key
            """,
            connection);
        command.Parameters.AddWithValue("key", RetentionRunner.AdvisoryLockKey);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var count = Convert.ToInt64(
                await command.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture);

            if (count > 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The retention advisory lock was never observed while the first run was executing.");
    }

    private sealed record MaintenanceRunRecord(
        int Days,
        DateTimeOffset StartedAt,
        DateTimeOffset FinishedAt,
        long DeletedEvents,
        long DeletedIssues,
        long DeletedMessages);
}

/// <summary>An <see cref="ILogger{T}"/> that records the structured log entries of a test.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<RecordingLoggerEntry> Entries { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state as IReadOnlyList<KeyValuePair<string, object?>>
            ?? [];

        Entries.Add(new RecordingLoggerEntry(
            formatter(state, exception),
            properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
    }
}

/// <summary>One recorded log entry with its structured properties.</summary>
internal sealed record RecordingLoggerEntry(
    string Message,
    IReadOnlyDictionary<string, object?> Properties);
