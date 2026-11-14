using System.Globalization;
using System.Net;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ManagementIssueMutationTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ProcessorFixture _fixture;

    public ManagementIssueMutationTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Issue_detail_includes_labels_and_the_latest_event_with_payload()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "detail-fingerprint");
        var latestEventId = await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddHours(1),
            "detail-fingerprint");

        var issueId = await IssueIdAsync(api, projectId);

        using (var patch = await api.PatchAsync($"/api/manage/issues/{issueId}", """{"labels":["beta","alpha"]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        using var response = await api.GetAsync($"/api/manage/issues/{issueId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var issue = await ManagementApiData.ReadJsonAsync(response);

        Assert.Equal(issueId, issue.GetProperty("id").GetInt64());
        Assert.Equal(
            "System.NullReferenceException: Object reference not set to an instance of an object",
            issue.GetProperty("title").GetString());
        Assert.Equal("Assembly-CSharp.Weapon.Fire", issue.GetProperty("culprit").GetString());
        Assert.Equal("error", issue.GetProperty("level").GetString());
        Assert.Equal("unity", issue.GetProperty("platform").GetString());
        Assert.Equal("unresolved", issue.GetProperty("status").GetString());
        Assert.Equal(2, issue.GetProperty("event_count").GetInt64());
        Assert.Equal("star-raiders@1.2.3", issue.GetProperty("last_release").GetString());
        Assert.Equal("production", issue.GetProperty("last_environment").GetString());
        Assert.Equal(BaseTime, ReadTimestamp(issue, "first_seen"));
        Assert.Equal(BaseTime.AddHours(1), ReadTimestamp(issue, "last_seen"));

        Assert.Equal(
            new[] { "alpha", "beta" },
            issue.GetProperty("labels").EnumerateArray()
                .Select(label => label.GetProperty("name").GetString()!));
        Assert.All(
            issue.GetProperty("labels").EnumerateArray(),
            label => Assert.Equal("#888888", label.GetProperty("color").GetString()));

        var latestEvent = issue.GetProperty("latest_event");
        Assert.Equal(latestEventId, latestEvent.GetProperty("id").GetGuid());
        Assert.Equal(issueId, latestEvent.GetProperty("issue_id").GetInt64());
        Assert.Equal(BaseTime.AddHours(1), ReadTimestamp(latestEvent, "timestamp"));
        Assert.Equal("error", latestEvent.GetProperty("level").GetString());
        Assert.Equal("unity", latestEvent.GetProperty("platform").GetString());
        Assert.Equal("production", latestEvent.GetProperty("environment").GetString());
        Assert.Equal("star-raiders@1.2.3", latestEvent.GetProperty("release").GetString());
        Assert.Equal("player-123", latestEvent.GetProperty("user_id").GetString());
        Assert.Equal("POST /api/1/events", latestEvent.GetProperty("endpoint").GetString());
        Assert.Equal(
            "System.NullReferenceException",
            latestEvent.GetProperty("payload").GetProperty("exception")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_issue_without_events_has_a_null_latest_event()
    {
        var projectId = TestData.NextProjectId();
        var issueId = await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            projectId,
            "Synthetic issue without events",
            BaseTime,
            BaseTime);

        using var api = new ManagementApiClient(_fixture);
        using var response = await api.GetAsync($"/api/manage/issues/{issueId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var issue = await ManagementApiData.ReadJsonAsync(response);

        Assert.Equal(0, issue.GetProperty("event_count").GetInt64());
        Assert.Empty(issue.GetProperty("labels").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, issue.GetProperty("latest_event").ValueKind);
    }

    [Fact]
    public async Task Unknown_issues_return_not_found()
    {
        using var api = new ManagementApiClient(_fixture);
        const long missingId = 9_000_000_000;

        using var get = await api.GetAsync($"/api/manage/issues/{missingId}");
        using var patch = await api.PatchAsync($"/api/manage/issues/{missingId}", """{"status":"resolved"}""");
        using var delete = await api.DeleteAsync($"/api/manage/issues/{missingId}");
        using var events = await api.GetAsync($"/api/manage/issues/{missingId}/events");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, events.StatusCode);
    }

    [Fact]
    public async Task Patch_changes_the_status_and_replaces_the_label_set()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "patch-fingerprint");
        var issueId = await IssueIdAsync(api, projectId);

        using (var resolved = await api.PatchAsync($"/api/manage/issues/{issueId}", """{"status":"resolved"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
            var body = await ManagementApiData.ReadJsonAsync(resolved);
            Assert.Equal("resolved", body.GetProperty("status").GetString());
            Assert.Empty(body.GetProperty("labels").EnumerateArray());
        }

        using (var labelled = await api.PatchAsync(
                   $"/api/manage/issues/{issueId}",
                   """{"labels":["alpha","beta","alpha"]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, labelled.StatusCode);
            var body = await ManagementApiData.ReadJsonAsync(labelled);
            Assert.Equal(
                new[] { "alpha", "beta" },
                body.GetProperty("labels").EnumerateArray()
                    .Select(label => label.GetProperty("name").GetString()!));
        }

        using (var replaced = await api.PatchAsync($"/api/manage/issues/{issueId}", """{"labels":["beta"]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
            var body = await ManagementApiData.ReadJsonAsync(replaced);
            Assert.Equal(
                new[] { "beta" },
                body.GetProperty("labels").EnumerateArray()
                    .Select(label => label.GetProperty("name").GetString()!));
        }

        // Replacing the label set must not delete label rows that are no
        // longer attached to the issue.
        using (var projectLabels = await api.GetAsync($"/api/manage/projects/{projectId}/labels"))
        {
            var labels = await ManagementApiData.ReadJsonAsync(projectLabels);
            Assert.Equal(
                new[] { "alpha", "beta" },
                labels.EnumerateArray().Select(label => label.GetProperty("name").GetString()!));
        }

        using (var cleared = await api.PatchAsync($"/api/manage/issues/{issueId}", """{"labels":[]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            var body = await ManagementApiData.ReadJsonAsync(cleared);
            Assert.Empty(body.GetProperty("labels").EnumerateArray());
            Assert.Equal("resolved", body.GetProperty("status").GetString());
        }

        using (var ignored = await api.PatchAsync(
                   $"/api/manage/issues/{issueId}",
                   """{"status":"ignored","labels":["gamma"]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
            var body = await ManagementApiData.ReadJsonAsync(ignored);
            Assert.Equal("ignored", body.GetProperty("status").GetString());
            Assert.Equal(
                new[] { "gamma" },
                body.GetProperty("labels").EnumerateArray()
                    .Select(label => label.GetProperty("name").GetString()!));
        }
    }

    [Theory]
    [InlineData("""{"status":"done"}""")]
    [InlineData("""{"labels":["  "]}""")]
    [InlineData("""{"labels":[null]}""")]
    [InlineData("""{not json""")]
    [InlineData("")]
    public async Task Invalid_patch_bodies_are_rejected(string body)
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "patch-invalid-fingerprint");
        var issueId = await IssueIdAsync(api, projectId);

        using var response = await api.SendAsync(
            HttpMethod.Patch,
            $"/api/manage/issues/{issueId}",
            json: body.Length == 0 ? null : body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_issue_cascades_to_its_events_and_label_assignments()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "delete-fingerprint");
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddMinutes(1), "delete-fingerprint");

        var issueId = await IssueIdAsync(api, projectId);

        using (var patch = await api.PatchAsync($"/api/manage/issues/{issueId}", """{"labels":["keep-me"]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        using (var delete = await api.DeleteAsync($"/api/manage/issues/{issueId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        Assert.Equal(0, await TestDatabase.CountIssuesAsync(_fixture.ConnectionString, projectId));
        Assert.Equal(0, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, projectId));
        Assert.Equal(
            0,
            await TestDatabase.QuerySingleLongAsync(
                _fixture.ConnectionString,
                $"select count(*) from issue_labels where issue_id = {issueId}"));

        using var get = await api.GetAsync($"/api/manage/issues/{issueId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        using var again = await api.DeleteAsync($"/api/manage/issues/{issueId}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        using var projectLabels = await api.GetAsync($"/api/manage/projects/{projectId}/labels");
        var labels = await ManagementApiData.ReadJsonAsync(projectLabels);
        Assert.Equal(
            new[] { "keep-me" },
            labels.EnumerateArray().Select(label => label.GetProperty("name").GetString()!));
    }

    [Fact]
    public async Task Bulk_actions_change_status_labels_and_delete_issues()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "bulk-1", release: "release-1");
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddMinutes(1), "bulk-2", release: "release-2");
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddMinutes(2), "bulk-3", release: "release-3");

        var ids = (await ManagementApiData.IssueIdsByReleaseAsync(api, projectId)).Values.ToArray();
        Assert.Equal(3, ids.Length);

        Assert.Equal(3, await BulkAsync(api, ids, "\"action\":\"resolve\""));
        Assert.Equal(3, (await ManagementApiData.GetIssuesAsync(api, projectId, "?status=resolved"))
            .GetProperty("items").GetArrayLength());

        Assert.Equal(3, await BulkAsync(api, ids, "\"action\":\"unresolve\""));
        Assert.Equal(3, (await ManagementApiData.GetIssuesAsync(api, projectId, "?status=unresolved"))
            .GetProperty("items").GetArrayLength());

        Assert.Equal(3, await BulkAsync(api, ids, "\"action\":\"ignore\""));
        Assert.Equal(3, (await ManagementApiData.GetIssuesAsync(api, projectId, "?status=ignored"))
            .GetProperty("items").GetArrayLength());

        Assert.Equal(3, await BulkAsync(api, ids, "\"action\":\"add_labels\",\"labels\":[\"triage\"]"));
        Assert.Equal(3, (await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&label=triage"))
            .GetProperty("items").GetArrayLength());
        Assert.Equal(3, await BulkAsync(api, ids, "\"action\":\"add_labels\",\"labels\":[\"triage\"]"));

        Assert.Equal(3, await BulkAsync(api, ids, "\"action\":\"remove_labels\",\"labels\":[\"triage\"]"));
        Assert.Empty((await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&label=triage"))
            .GetProperty("items").EnumerateArray());
        Assert.Equal(0, await BulkAsync(api, ids, "\"action\":\"remove_labels\",\"labels\":[\"triage\"]"));

        Assert.Equal(3, await BulkAsync(api, ids, "\"action\":\"delete\""));
        Assert.Equal(0, await TestDatabase.CountIssuesAsync(_fixture.ConnectionString, projectId));
        Assert.Equal(0, await TestDatabase.CountEventsAsync(_fixture.ConnectionString, projectId));
    }

    [Fact]
    public async Task Bulk_actions_ignore_unknown_ids()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "partial-bulk");
        var issueId = await IssueIdAsync(api, projectId);

        Assert.Equal(
            1,
            await BulkAsync(api, [issueId, 9_000_000_000], "\"action\":\"resolve\""));

        var resolved = await ManagementApiData.GetIssuesAsync(api, projectId, "?status=resolved");
        Assert.Equal(issueId, resolved.GetProperty("items")[0].GetProperty("id").GetInt64());

        Assert.Equal(
            1,
            await BulkAsync(api, [issueId, 9_000_000_000], "\"action\":\"add_labels\",\"labels\":[\"partial\"]"));
        Assert.Single(
            (await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&label=partial"))
            .GetProperty("items")
            .EnumerateArray());
    }

    [Fact]
    public async Task Invalid_bulk_requests_are_rejected()
    {
        using var api = new ManagementApiClient(_fixture);
        var tooManyIds = string.Join(
            ",",
            Enumerable.Range(1, 501).Select(value => value.ToString(CultureInfo.InvariantCulture)));

        var bodies = new[]
        {
            "{}",
            """{"ids":[],"action":"resolve"}""",
            """{"ids":[1],"action":"explode"}""",
            """{"ids":[1],"action":"add_labels"}""",
            """{"ids":[1],"action":"add_labels","labels":[]}""",
            """{"ids":[1],"action":"remove_labels","labels":[""]}""",
            $$"""{"ids":[{{tooManyIds}}],"action":"resolve"}""",
            """{not json""",
        };

        foreach (var body in bodies)
        {
            using var response = await api.PostAsync("/api/manage/issues/bulk", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var parsed = await ManagementApiData.ReadJsonAsync(response);
            Assert.False(string.IsNullOrWhiteSpace(parsed.GetProperty("error").GetString()));
        }
    }

    private static async Task<long> BulkAsync(ManagementApiClient api, long[] ids, string body)
    {
        var idList = string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));

        using var response = await api.PostAsync(
            "/api/manage/issues/bulk",
            $$"""{"ids":[{{idList}}],{{body}}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var parsed = await ManagementApiData.ReadJsonAsync(response);
        return parsed.GetProperty("affected").GetInt64();
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
