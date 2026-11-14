using System.Net;
using System.Text.Json;
using GlitchPort.Collector.Contracts;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class EventStoreTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ProcessorFixture _fixture;

    public EventStoreTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Two_identical_exceptions_produce_one_issue_and_two_events()
    {
        var projectId = TestData.NextProjectId();
        var payload = TestData.CreateExceptionPayload();

        using var client = _fixture.CreateClient();

        var first = TestData.CreatePayloadEnvelope(Guid.NewGuid(), projectId, BaseTime, payload);
        var second = TestData.CreatePayloadEnvelope(
            Guid.NewGuid(),
            projectId,
            BaseTime.AddMinutes(1),
            payload);

        using var response = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [first, second]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, await TestData.ReadStoredAsync(response));

        var issue = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId);

        Assert.Equal(1, await TestDatabase.CountIssuesAsync(_fixture.ConnectionString, projectId));
        Assert.Equal(2, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, projectId));
        Assert.Equal(2, issue.EventCount);
        Assert.Equal("unresolved", issue.Status);
        Assert.Equal("error", issue.Level);
        Assert.Equal("unity", issue.Platform);
        Assert.Equal(
            "System.NullReferenceException: Object reference not set to an instance of an object",
            issue.Title);
        Assert.Equal("Assembly-CSharp.Weapon.Fire", issue.Culprit);
        Assert.Equal("star-raiders@1.2.3", issue.LastRelease);
        Assert.Equal("production", issue.LastEnvironment);
        Assert.Equal(BaseTime, issue.FirstSeen);
        Assert.Equal(BaseTime.AddMinutes(1), issue.LastSeen);

        var stored = await TestDatabase.GetEventAsync(_fixture.ConnectionString, first.EventId);

        Assert.Equal(projectId, stored.ProjectId);
        Assert.Equal(issue.Id, stored.IssueId);
        Assert.Equal(BaseTime, stored.Timestamp);
        Assert.Equal(BaseTime, stored.ReceivedAt);
        Assert.Equal("error", stored.Level);
        Assert.Equal("unity", stored.Platform);
        Assert.Equal("production", stored.Environment);
        Assert.Equal("star-raiders@1.2.3", stored.Release);
        Assert.Equal("player-123", stored.UserId);
        Assert.Equal("POST /api/1/events", stored.Endpoint);
        Assert.Equal(
            "NullReferenceException: Object reference not set to an instance of an object",
            stored.Message);

        using var eventPayload = JsonDocument.Parse(stored.PayloadJson);
        Assert.Equal(
            "System.NullReferenceException",
            eventPayload.RootElement
                .GetProperty("exception")[0]
                .GetProperty("type")
                .GetString());
        Assert.Equal(
            "Object reference not set to an instance of an object",
            eventPayload.RootElement.GetProperty("exception")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Resending_the_same_event_id_stores_it_once()
    {
        var projectId = TestData.NextProjectId();
        var envelope = TestData.CreatePayloadEnvelope(
            Guid.NewGuid(),
            projectId,
            BaseTime,
            TestData.CreateExceptionPayload());

        using var client = _fixture.CreateClient();

        using var firstResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope]);
        Assert.Equal(1, await TestData.ReadStoredAsync(firstResponse));

        using var secondResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope]);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(0, await TestData.ReadStoredAsync(secondResponse));
        Assert.Equal(1, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, projectId));

        var issue = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId);
        Assert.Equal(1, issue.EventCount);
    }

    [Fact]
    public async Task Duplicate_event_ids_inside_a_batch_are_stored_once()
    {
        var projectId = TestData.NextProjectId();
        var envelope = TestData.CreatePayloadEnvelope(
            Guid.NewGuid(),
            projectId,
            BaseTime,
            TestData.CreateExceptionPayload());

        using var client = _fixture.CreateClient();

        using var response = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope, envelope with { ReceivedAt = BaseTime.AddMinutes(5) }]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await TestData.ReadStoredAsync(response));
        Assert.Equal(1, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, projectId));
        Assert.Equal(1, (await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId)).EventCount);
    }

    [Fact]
    public async Task A_resolved_issue_reopens_on_a_new_event()
    {
        var projectId = TestData.NextProjectId();
        var payload = TestData.CreateExceptionPayload();

        using var client = _fixture.CreateClient();

        using var firstResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [TestData.CreatePayloadEnvelope(Guid.NewGuid(), projectId, BaseTime, payload)]);
        Assert.Equal(1, await TestData.ReadStoredAsync(firstResponse));

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"update issues set status = 'resolved' where project_id = {projectId}");

        using var secondResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [TestData.CreatePayloadEnvelope(Guid.NewGuid(), projectId, BaseTime.AddHours(1), payload)]);
        Assert.Equal(1, await TestData.ReadStoredAsync(secondResponse));

        var issue = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId);

        Assert.Equal("unresolved", issue.Status);
        Assert.Equal(2, issue.EventCount);
        Assert.Equal(BaseTime, issue.FirstSeen);
        Assert.Equal(BaseTime.AddHours(1), issue.LastSeen);
    }

    [Fact]
    public async Task An_ignored_issue_stays_ignored()
    {
        var projectId = TestData.NextProjectId();
        var payload = TestData.CreateExceptionPayload();

        using var client = _fixture.CreateClient();

        using var firstResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [TestData.CreatePayloadEnvelope(Guid.NewGuid(), projectId, BaseTime, payload)]);
        Assert.Equal(1, await TestData.ReadStoredAsync(firstResponse));

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"update issues set status = 'ignored' where project_id = {projectId}");

        using var secondResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [TestData.CreatePayloadEnvelope(Guid.NewGuid(), projectId, BaseTime.AddHours(1), payload)]);
        Assert.Equal(1, await TestData.ReadStoredAsync(secondResponse));

        var issue = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId);

        Assert.Equal("ignored", issue.Status);
        Assert.Equal(2, issue.EventCount);
    }

    [Fact]
    public async Task Level_is_raised_to_the_most_severe_event_and_never_lowered()
    {
        var projectId = TestData.NextProjectId();

        using var client = _fixture.CreateClient();

        using var infoResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime,
                    TestData.CreateExceptionPayload(level: "info")),
            ]);
        Assert.Equal(1, await TestData.ReadStoredAsync(infoResponse));

        var afterInfo = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId);
        Assert.Equal("info", afterInfo.Level);

        using var warningResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddMinutes(1),
                    TestData.CreateExceptionPayload(level: "warning")),
            ]);
        Assert.Equal(1, await TestData.ReadStoredAsync(warningResponse));
        Assert.Equal(
            "warning",
            (await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId)).Level);

        using var fatalResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddMinutes(2),
                    TestData.CreateExceptionPayload(level: "fatal")),
            ]);
        Assert.Equal(1, await TestData.ReadStoredAsync(fatalResponse));
        Assert.Equal(
            "fatal",
            (await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId)).Level);

        using var debugResponse = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [
                TestData.CreatePayloadEnvelope(
                    Guid.NewGuid(),
                    projectId,
                    BaseTime.AddMinutes(3),
                    TestData.CreateExceptionPayload(level: "debug")),
            ]);
        Assert.Equal(1, await TestData.ReadStoredAsync(debugResponse));

        var issue = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId);
        Assert.Equal("fatal", issue.Level);
        Assert.Equal(4, issue.EventCount);
    }

    [Fact]
    public async Task An_exception_only_event_stores_no_message_and_stays_out_of_the_message_list()
    {
        var projectId = TestData.NextProjectId();
        var envelope = TestData.CreatePayloadEnvelope(
            Guid.NewGuid(),
            projectId,
            BaseTime,
            TestData.CreateExceptionPayload(message: null));

        using var client = _fixture.CreateClient();

        using var response = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await TestData.ReadStoredAsync(response));

        var stored = await TestDatabase.GetEventAsync(_fixture.ConnectionString, envelope.EventId);
        Assert.Null(stored.Message);

        using var request = TestData.CreateAuthorizedGet("/api/manage/messages?limit=500");
        using var listResponse = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        using var document = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            document.RootElement.EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == envelope.EventId);
    }

    [Fact]
    public async Task A_v1_envelope_is_converted_to_an_info_event()
    {
        var projectId = TestData.NextProjectId();
        var clientTimestamp = BaseTime.AddMinutes(-10);
        var envelope = new Envelope
        {
            Version = 1,
            EventId = Guid.NewGuid(),
            ProjectId = projectId,
            ReceivedAt = BaseTime,
            Endpoint = "POST /api/1/messages",
            Message = "The game crashed while loading the level",
            ClientTimestamp = clientTimestamp,
            SdkName = "glitch-port-dotnet",
            SdkVersion = "1.2.3",
        };

        using var client = _fixture.CreateClient();

        using var response = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await TestData.ReadStoredAsync(response));

        var stored = await TestDatabase.GetEventAsync(_fixture.ConnectionString, envelope.EventId);

        Assert.Equal("info", stored.Level);
        Assert.Equal("other", stored.Platform);
        Assert.Equal("The game crashed while loading the level", stored.Message);
        Assert.Equal(clientTimestamp, stored.Timestamp);
        Assert.Equal(BaseTime, stored.ReceivedAt);

        using var payload = JsonDocument.Parse(stored.PayloadJson);
        Assert.Equal("info", payload.RootElement.GetProperty("level").GetString());
        Assert.Equal(
            "The game crashed while loading the level",
            payload.RootElement.GetProperty("message").GetString());
        Assert.Equal("glitch-port-dotnet", payload.RootElement.GetProperty("sdk").GetProperty("name").GetString());
        Assert.Equal("1.2.3", payload.RootElement.GetProperty("sdk").GetProperty("version").GetString());
        Assert.Equal(
            clientTimestamp,
            DateTimeOffset.Parse(
                payload.RootElement.GetProperty("timestamp").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture));

        var issue = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId);
        Assert.Equal("The game crashed while loading the level", issue.Title);
        Assert.Equal("info", issue.Level);
        Assert.Equal("other", issue.Platform);
    }

    [Fact]
    public async Task Two_processor_instances_store_a_shared_fingerprint_without_duplicates()
    {
        const int uniqueEventCount = 150;
        const int duplicateEventCount = 50;

        var projectId = TestData.NextProjectId();
        var payload = TestData.CreateExceptionPayload();

        using var firstFactory = new ProcessorApplicationFactory(_fixture.ConnectionString);
        using var secondFactory = new ProcessorApplicationFactory(_fixture.ConnectionString);
        using var firstClient = firstFactory.CreateClient();
        using var secondClient = secondFactory.CreateClient();

        var events = Enumerable
            .Range(0, uniqueEventCount)
            .Select(index => TestData.CreatePayloadEnvelope(
                Guid.NewGuid(),
                projectId,
                BaseTime.AddSeconds(index),
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

        Assert.Equal(100, firstBatch.Length);
        Assert.Equal(100, secondBatch.Length);

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

        var issue = await TestDatabase.GetIssueAsync(_fixture.ConnectionString, projectId);

        Assert.Equal(1, await TestDatabase.CountIssuesAsync(_fixture.ConnectionString, projectId));
        Assert.Equal(uniqueEventCount, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, projectId));
        Assert.Equal(uniqueEventCount, issue.EventCount);
    }
}
