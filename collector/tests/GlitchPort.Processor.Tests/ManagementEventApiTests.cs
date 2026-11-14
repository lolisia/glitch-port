using System.Globalization;
using System.Net;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ManagementEventApiTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ProcessorFixture _fixture;

    public ManagementEventApiTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Issue_events_are_listed_newest_first_and_paginated()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        var oldest = await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "event-list");
        var middle = await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddHours(1), "event-list");
        var newest = await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddHours(2), "event-list");
        var issueId = await IssueIdAsync(api, projectId);

        using var first = await api.GetAsync($"/api/manage/issues/{issueId}/events?limit=2");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var firstPage = await ManagementApiData.ReadJsonAsync(first);
        var firstItems = firstPage.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, firstItems.Count);
        Assert.Equal(newest, firstItems[0].GetProperty("id").GetGuid());
        Assert.Equal(middle, firstItems[1].GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(firstPage.GetProperty("next_cursor").GetString()));

        var newestItem = firstItems[0];
        Assert.Equal(BaseTime.AddHours(2), ReadTimestamp(newestItem, "timestamp"));
        Assert.Equal(BaseTime.AddHours(2), ReadTimestamp(newestItem, "received_at"));
        Assert.Equal("error", newestItem.GetProperty("level").GetString());
        Assert.Equal("production", newestItem.GetProperty("environment").GetString());
        Assert.Equal("star-raiders@1.2.3", newestItem.GetProperty("release").GetString());
        Assert.Equal("player-123", newestItem.GetProperty("user_id").GetString());
        Assert.Equal(
            "NullReferenceException: Object reference not set to an instance of an object",
            newestItem.GetProperty("message").GetString());

        var cursor = firstPage.GetProperty("next_cursor").GetString()!;
        using var second = await api.GetAsync(
            $"/api/manage/issues/{issueId}/events?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var secondPage = await ManagementApiData.ReadJsonAsync(second);
        var secondItems = secondPage.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(secondItems);
        Assert.Equal(oldest, secondItems[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("next_cursor").ValueKind);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=not-a-number")]
    [InlineData("cursor=not-a-cursor")]
    public async Task Invalid_event_list_parameters_are_rejected(string query)
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.GetAsync($"/api/manage/issues/1/events?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Events_of_an_unknown_issue_return_not_found()
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.GetAsync("/api/manage/issues/9000000000/events");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Event_detail_returns_the_full_payload()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        var eventId = await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "event-detail");
        var issueId = await IssueIdAsync(api, projectId);

        using var response = await api.GetAsync($"/api/manage/events/{eventId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var storedEvent = await ManagementApiData.ReadJsonAsync(response);

        Assert.Equal(eventId, storedEvent.GetProperty("id").GetGuid());
        Assert.Equal(projectId, storedEvent.GetProperty("project_id").GetInt64());
        Assert.Equal(issueId, storedEvent.GetProperty("issue_id").GetInt64());
        Assert.Equal(BaseTime, ReadTimestamp(storedEvent, "timestamp"));
        Assert.Equal(BaseTime, ReadTimestamp(storedEvent, "received_at"));
        Assert.Equal("error", storedEvent.GetProperty("level").GetString());
        Assert.Equal("unity", storedEvent.GetProperty("platform").GetString());
        Assert.Equal("production", storedEvent.GetProperty("environment").GetString());
        Assert.Equal("star-raiders@1.2.3", storedEvent.GetProperty("release").GetString());
        Assert.Equal("player-123", storedEvent.GetProperty("user_id").GetString());
        Assert.Equal("POST /api/1/events", storedEvent.GetProperty("endpoint").GetString());
        Assert.Equal(
            "NullReferenceException: Object reference not set to an instance of an object",
            storedEvent.GetProperty("message").GetString());

        var payload = storedEvent.GetProperty("payload");
        Assert.Equal("unity", payload.GetProperty("platform").GetString());
        Assert.Equal(
            "System.NullReferenceException",
            payload.GetProperty("exception")[0].GetProperty("type").GetString());
        Assert.Equal(
            "Weapon.Fire",
            payload.GetProperty("exception")[0]
                .GetProperty("stacktrace").GetProperty("frames")[2].GetProperty("function").GetString());
        Assert.Equal(
            "Assembly-CSharp",
            payload.GetProperty("exception")[0]
                .GetProperty("stacktrace").GetProperty("frames")[2].GetProperty("module").GetString());
    }

    [Fact]
    public async Task Unknown_or_malformed_event_ids_return_not_found()
    {
        using var api = new ManagementApiClient(_fixture);

        using var unknown = await api.GetAsync($"/api/manage/events/{Guid.NewGuid()}");
        using var malformed = await api.GetAsync("/api/manage/events/not-a-guid");
        using var deleteUnknown = await api.DeleteAsync($"/api/manage/events/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteUnknown.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_event_decrements_the_issue_count_without_deleting_the_issue()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        var older = await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "event-delete");
        var newer = await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddHours(1),
            "event-delete");
        var issueId = await IssueIdAsync(api, projectId);

        using (var delete = await api.DeleteAsync($"/api/manage/events/{older}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        using (var response = await api.GetAsync($"/api/manage/issues/{issueId}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var issue = await ManagementApiData.ReadJsonAsync(response);
            Assert.Equal(1, issue.GetProperty("event_count").GetInt64());
            Assert.Equal(newer, issue.GetProperty("latest_event").GetProperty("id").GetGuid());
        }

        using (var delete = await api.DeleteAsync($"/api/manage/events/{newer}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        using (var response = await api.GetAsync($"/api/manage/issues/{issueId}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var issue = await ManagementApiData.ReadJsonAsync(response);
            Assert.Equal(0, issue.GetProperty("event_count").GetInt64());
            Assert.Equal(JsonValueKind.Null, issue.GetProperty("latest_event").ValueKind);
        }

        using (var deleted = await api.GetAsync($"/api/manage/events/{newer}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }

        using (var again = await api.DeleteAsync($"/api/manage/events/{newer}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        using (var events = await api.GetAsync($"/api/manage/issues/{issueId}/events"))
        {
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            var page = await ManagementApiData.ReadJsonAsync(events);
            Assert.Empty(page.GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public async Task Deleting_an_event_never_makes_the_issue_count_negative()
    {
        var projectId = TestData.NextProjectId();
        var issueId = await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            projectId,
            "Issue with an inconsistent count",
            BaseTime,
            BaseTime,
            eventCount: 0);
        var eventId = Guid.NewGuid();

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $$"""
             insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, endpoint, payload)
             values ('{{eventId}}', {{projectId}}, {{issueId}}, '2026-08-01T12:00:00Z', '2026-08-01T12:00:00Z',
                     'error', 'unity', 'POST /api/1/events', '{}'::jsonb)
             """);

        using var api = new ManagementApiClient(_fixture);
        using var delete = await api.DeleteAsync($"/api/manage/events/{eventId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Equal(
            0,
            await TestDatabase.QuerySingleLongAsync(
                _fixture.ConnectionString,
                $"select event_count from issues where id = {issueId}"));
    }

    private async Task<long> IssueIdAsync(ManagementApiClient api, long projectId)
    {
        var page = await ManagementApiData.GetIssuesAsync(api, projectId);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        return Assert.Single(items).GetProperty("id").GetInt64();
    }

    private static DateTimeOffset ReadTimestamp(JsonElement element, string propertyName) =>
        DateTimeOffset.Parse(
            element.GetProperty(propertyName).GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
