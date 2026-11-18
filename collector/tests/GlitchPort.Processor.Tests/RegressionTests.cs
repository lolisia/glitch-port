using System.Net;
using GlitchPort.Collector.Contracts;
using Npgsql;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// Regression detection and release-aware resolution in the event store.
/// </summary>
[Collection(ProcessorCollection.Name)]
public sealed class RegressionTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ProcessorFixture _fixture;

    public RegressionTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_resolved_issue_regresses_on_the_next_event()
    {
        var projectId = TestData.NextProjectId();
        var payload = TestData.CreateExceptionPayload();

        using var client = _fixture.CreateClient();

        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(Guid.NewGuid(), projectId, BaseTime, payload)));

        await ResolveDirectlyAsync(projectId, resolvedInRelease: null);

        var regressionTime = BaseTime.AddHours(1);
        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    regressionTime,
                    payload)));

        var issue = await ReadIssueAsync(projectId);

        Assert.Equal("unresolved", issue.Status);
        Assert.Equal(2, issue.EventCount);
        Assert.Equal(1, issue.RegressionCount);
        Assert.Equal(regressionTime, issue.RegressedAt);
        Assert.Null(issue.ResolvedAt);
        Assert.Null(issue.ResolvedInRelease);
    }

    [Fact]
    public async Task Resending_an_event_does_not_increment_the_regression_count()
    {
        var projectId = TestData.NextProjectId();
        var envelope = TestData.CreatePayloadEnvelope(
            Guid.NewGuid(),
            projectId,
            BaseTime,
            TestData.CreateExceptionPayload());

        using var client = _fixture.CreateClient();

        Assert.Equal(1, await StoreAsync(client, envelope));
        await ResolveDirectlyAsync(projectId, resolvedInRelease: null);

        // The duplicate event row is suppressed, so it must not reopen the
        // issue either.
        Assert.Equal(0, await StoreAsync(client, envelope));

        var issue = await ReadIssueAsync(projectId);

        Assert.Equal("resolved", issue.Status);
        Assert.Equal(1, issue.EventCount);
        Assert.Equal(0, issue.RegressionCount);
        Assert.Null(issue.RegressedAt);
    }

    [Fact]
    public async Task An_event_from_the_resolved_release_keeps_the_issue_resolved()
    {
        var projectId = TestData.NextProjectId();

        using var client = _fixture.CreateClient();

        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime,
                    TestData.CreateExceptionPayload(release: "star-raiders@1.0.0"))));

        await ResolveDirectlyAsync(projectId, resolvedInRelease: "star-raiders@1.0.0");

        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddHours(1),
                    TestData.CreateExceptionPayload(release: "star-raiders@1.0.0"))));

        var issue = await ReadIssueAsync(projectId);

        Assert.Equal("resolved", issue.Status);
        Assert.Equal("star-raiders@1.0.0", issue.ResolvedInRelease);
        Assert.NotNull(issue.ResolvedAt);
        Assert.Equal(0, issue.RegressionCount);
        Assert.Null(issue.RegressedAt);
        Assert.Equal(2, issue.EventCount);
    }

    [Fact]
    public async Task An_event_from_a_different_release_reopens_the_issue()
    {
        var projectId = TestData.NextProjectId();

        using var client = _fixture.CreateClient();

        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime,
                    TestData.CreateExceptionPayload(release: "star-raiders@1.0.0"))));

        await ResolveDirectlyAsync(projectId, resolvedInRelease: "star-raiders@1.0.0");

        var regressionTime = BaseTime.AddHours(1);
        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    regressionTime,
                    TestData.CreateExceptionPayload(release: "star-raiders@1.1.0"))));

        var issue = await ReadIssueAsync(projectId);

        Assert.Equal("unresolved", issue.Status);
        Assert.Equal(1, issue.RegressionCount);
        Assert.Equal(regressionTime, issue.RegressedAt);
        Assert.Null(issue.ResolvedAt);
        Assert.Null(issue.ResolvedInRelease);
        Assert.Equal("star-raiders@1.1.0", issue.LastRelease);
    }

    [Fact]
    public async Task A_release_less_event_does_not_reopen_an_issue_resolved_in_a_release()
    {
        var projectId = TestData.NextProjectId();

        using var client = _fixture.CreateClient();

        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime,
                    TestData.CreateExceptionPayload(release: "star-raiders@1.0.0"))));

        await ResolveDirectlyAsync(projectId, resolvedInRelease: "star-raiders@1.0.0");

        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddHours(1),
                    TestData.CreateExceptionPayload(release: null))));

        var issue = await ReadIssueAsync(projectId);

        Assert.Equal("resolved", issue.Status);
        Assert.Equal("star-raiders@1.0.0", issue.ResolvedInRelease);
        Assert.Equal(0, issue.RegressionCount);
        Assert.Null(issue.RegressedAt);
    }

    [Fact]
    public async Task A_new_issue_takes_the_release_of_its_earliest_event()
    {
        var projectId = TestData.NextProjectId();

        using var client = _fixture.CreateClient();

        Assert.Equal(
            2,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddHours(2),
                    TestData.CreateExceptionPayload(
                        release: "star-raiders@2.0.0",
                        timestamp: BaseTime.AddHours(1))),
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddHours(3),
                    TestData.CreateExceptionPayload(
                        release: "star-raiders@1.0.0",
                        timestamp: BaseTime))));

        var issue = await ReadIssueAsync(projectId);

        Assert.Equal(BaseTime, issue.FirstSeen);
        Assert.Equal("star-raiders@1.0.0", issue.FirstRelease);
        Assert.Equal("star-raiders@2.0.0", issue.LastRelease);
    }

    [Fact]
    public async Task A_genuinely_earlier_event_arriving_late_advances_first_release()
    {
        var projectId = TestData.NextProjectId();

        using var client = _fixture.CreateClient();

        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddHours(2),
                    TestData.CreateExceptionPayload(
                        release: "star-raiders@2.0.0",
                        timestamp: BaseTime.AddHours(1)))));

        var before = await ReadIssueAsync(projectId);
        Assert.Equal(BaseTime.AddHours(1), before.FirstSeen);
        Assert.Equal("star-raiders@2.0.0", before.FirstRelease);

        // The event arrives after the newer one but describes an earlier
        // moment, so it moves first_seen and first_release back without
        // touching last_seen or last_release.
        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddHours(3),
                    TestData.CreateExceptionPayload(
                        release: "star-raiders@1.0.0",
                        timestamp: BaseTime))));

        var issue = await ReadIssueAsync(projectId);

        Assert.Equal(BaseTime, issue.FirstSeen);
        Assert.Equal(BaseTime.AddHours(1), issue.LastSeen);
        Assert.Equal("star-raiders@1.0.0", issue.FirstRelease);
        Assert.Equal("star-raiders@2.0.0", issue.LastRelease);
    }

    [Fact]
    public async Task A_late_event_that_does_not_advance_first_seen_keeps_first_release()
    {
        var projectId = TestData.NextProjectId();

        using var client = _fixture.CreateClient();

        Assert.Equal(
            2,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime,
                    TestData.CreateExceptionPayload(
                        release: "star-raiders@1.0.0",
                        timestamp: BaseTime)),
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddHours(3),
                    TestData.CreateExceptionPayload(
                        release: "star-raiders@3.0.0",
                        timestamp: BaseTime.AddHours(2)))));

        Assert.Equal(
            1,
            await StoreAsync(
                client,
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddHours(4),
                    TestData.CreateExceptionPayload(
                        release: "star-raiders@2.0.0",
                        timestamp: BaseTime.AddHours(1)))));

        var issue = await ReadIssueAsync(projectId);

        Assert.Equal(BaseTime, issue.FirstSeen);
        Assert.Equal("star-raiders@1.0.0", issue.FirstRelease);
        Assert.Equal(BaseTime.AddHours(2), issue.LastSeen);
        Assert.Equal("star-raiders@3.0.0", issue.LastRelease);
    }

    [Fact]
    public async Task Two_processor_instances_regress_a_resolved_issue_exactly_once()
    {
        const int uniqueEventCount = 150;
        const int duplicateEventCount = 50;

        var projectId = TestData.NextProjectId();
        var payload = TestData.CreateExceptionPayload(release: "star-raiders@1.0.0");

        using var seedClient = _fixture.CreateClient();

        Assert.Equal(
            1,
            await StoreAsync(
                seedClient,
                TestData.CreatePayloadEnvelope(Guid.NewGuid(), projectId, BaseTime, payload)));

        await ResolveDirectlyAsync(projectId, resolvedInRelease: null);

        using var firstFactory = new ProcessorApplicationFactory(_fixture.ConnectionString);
        using var secondFactory = new ProcessorApplicationFactory(_fixture.ConnectionString);
        using var firstClient = firstFactory.CreateClient();
        using var secondClient = secondFactory.CreateClient();

        var events = Enumerable
            .Range(0, uniqueEventCount)
            .Select(index => TestData.CreatePayloadEnvelope(
                Guid.NewGuid(),
                projectId,
                BaseTime.AddSeconds(1 + index),
                payload))
            .ToArray();

        // The second instance resends the first 50 event ids to force
        // cross-instance duplicate handling.
        var duplicates = events
            .Take(duplicateEventCount)
            .Select(envelope => envelope with { ReceivedAt = envelope.ReceivedAt.AddMinutes(10) })
            .ToArray();

        var firstBatch = events.Take(100).ToArray();
        var secondBatch = events.Skip(100).Concat(duplicates).ToArray();

        var responses = await Task.WhenAll(
            TestData.PostEnvelopesAsync(
                firstClient,
                ProcessorApplicationFactory.InternalSharedSecret,
                firstBatch),
            TestData.PostEnvelopesAsync(
                secondClient,
                ProcessorApplicationFactory.InternalSharedSecret,
                secondBatch));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

            var stored = 0;
            foreach (var response in responses)
            {
                stored += await TestData.ReadStoredAsync(response);
            }

            Assert.Equal(uniqueEventCount, stored);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        var issue = await ReadIssueAsync(projectId);

        Assert.Equal("unresolved", issue.Status);
        Assert.Equal(1, issue.RegressionCount);
        Assert.NotNull(issue.RegressedAt);
        Assert.Equal(1 + uniqueEventCount, issue.EventCount);
        Assert.Equal(1 + uniqueEventCount, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, projectId));
    }

    private async Task<int> StoreAsync(HttpClient client, params Envelope[] envelopes)
    {
        using var response = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            envelopes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestData.ReadStoredAsync(response);
    }

    private async Task ResolveDirectlyAsync(long projectId, string? resolvedInRelease)
    {
        var resolvedInReleaseSql = resolvedInRelease is null
            ? "null"
            : $"'{resolvedInRelease.Replace("'", "''", StringComparison.Ordinal)}'";

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
             update issues
             set status = 'resolved',
                 resolved_at = now(),
                 resolved_in_release = {resolvedInReleaseSql}
             where project_id = {projectId}
             """);
    }

    private async Task<RegressionIssueRow> ReadIssueAsync(long projectId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select id,
                   status,
                   first_seen,
                   last_seen,
                   event_count,
                   first_release,
                   last_release,
                   resolved_at,
                   resolved_in_release,
                   regressed_at,
                   regression_count
            from issues
            where project_id = @project_id
            """,
            connection);

        command.Parameters.AddWithValue("project_id", projectId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No issue exists for project {projectId}.");
        }

        return new RegressionIssueRow
        {
            Status = reader.GetString(1),
            FirstSeen = ReadTimestamp(reader, 2),
            LastSeen = ReadTimestamp(reader, 3),
            EventCount = reader.GetInt64(4),
            FirstRelease = reader.IsDBNull(5) ? null : reader.GetString(5),
            LastRelease = reader.IsDBNull(6) ? null : reader.GetString(6),
            ResolvedAt = ReadNullableTimestamp(reader, 7),
            ResolvedInRelease = reader.IsDBNull(8) ? null : reader.GetString(8),
            RegressedAt = ReadNullableTimestamp(reader, 9),
            RegressionCount = reader.GetInt32(10),
        };
    }

    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private static DateTimeOffset? ReadNullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadTimestamp(reader, ordinal);

    private sealed class RegressionIssueRow
    {
        public string Status { get; init; } = string.Empty;

        public DateTimeOffset FirstSeen { get; init; }

        public DateTimeOffset LastSeen { get; init; }

        public long EventCount { get; init; }

        public string? FirstRelease { get; init; }

        public string? LastRelease { get; init; }

        public DateTimeOffset? ResolvedAt { get; init; }

        public string? ResolvedInRelease { get; init; }

        public DateTimeOffset? RegressedAt { get; init; }

        public int RegressionCount { get; init; }
    }
}
