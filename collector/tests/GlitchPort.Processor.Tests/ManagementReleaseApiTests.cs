using System.Net;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ManagementReleaseApiTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ProcessorFixture _fixture;

    public ManagementReleaseApiTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Releases_are_aggregated_and_sorted_by_last_seen_then_release()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "release-a", release: "1.0.0");
        await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddHours(1),
            "release-b",
            release: "1.0.0");
        await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddHours(1),
            "release-c",
            release: "0.9.0");
        await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddHours(2),
            "release-a",
            release: "2.0.0");

        var page = await GetReleasesAsync(api, projectId);
        var items = page.GetProperty("items").EnumerateArray().ToList();

        // 2.0.0 has the newest event; 1.0.0 and 0.9.0 share last_seen, so the
        // release name breaks the tie ascending.
        Assert.Equal(
            new[] { "2.0.0", "0.9.0", "1.0.0" },
            items.Select(item => item.GetProperty("release").GetString()!));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next_cursor").ValueKind);

        var newest = items[0];
        Assert.Equal(BaseTime.AddHours(2), newest.GetProperty("first_seen").GetDateTimeOffset());
        Assert.Equal(BaseTime.AddHours(2), newest.GetProperty("last_seen").GetDateTimeOffset());
        Assert.Equal(1, newest.GetProperty("event_count").GetInt64());
        Assert.Equal(1, newest.GetProperty("issue_count").GetInt64());
        Assert.Equal(0, newest.GetProperty("new_issue_count").GetInt64());
        Assert.Equal(1, newest.GetProperty("unresolved_issue_count").GetInt64());

        var firstRelease = items[2];
        Assert.Equal(BaseTime, firstRelease.GetProperty("first_seen").GetDateTimeOffset());
        Assert.Equal(BaseTime.AddHours(1), firstRelease.GetProperty("last_seen").GetDateTimeOffset());
        Assert.Equal(2, firstRelease.GetProperty("event_count").GetInt64());
        Assert.Equal(2, firstRelease.GetProperty("issue_count").GetInt64());
        Assert.Equal(2, firstRelease.GetProperty("new_issue_count").GetInt64());
        Assert.Equal(2, firstRelease.GetProperty("unresolved_issue_count").GetInt64());
    }

    [Fact]
    public async Task Unresolved_issue_count_follows_the_current_issue_status()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "status-a", release: "1.0.0");
        await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddMinutes(1),
            "status-b",
            release: "1.0.0");

        var issues = await ManagementApiData.GetIssuesAsync(api, projectId);
        var issuesById = issues.GetProperty("items").EnumerateArray()
            .OrderBy(item => item.GetProperty("id").GetInt64())
            .ToList();
        Assert.Equal(2, issuesById.Count);

        // Resolve one of the two issues of the release.
        var resolvedIssueId = issuesById[0].GetProperty("id").GetInt64();

        using (var patch = await api.PatchAsync($"/api/manage/issues/{resolvedIssueId}", """{"status":"resolved"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        var page = await GetReleasesAsync(api, projectId);
        var release = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(2, release.GetProperty("event_count").GetInt64());
        Assert.Equal(2, release.GetProperty("issue_count").GetInt64());
        Assert.Equal(1, release.GetProperty("unresolved_issue_count").GetInt64());
    }

    [Fact]
    public async Task Releases_can_be_paged_with_the_cursor()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "page-a", release: "1.0.0");
        await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddMinutes(1),
            "page-b",
            release: "2.0.0");
        await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddMinutes(2),
            "page-c",
            release: "3.0.0");

        var first = await GetReleasesAsync(api, projectId, "?limit=2");
        var firstItems = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "3.0.0", "2.0.0" },
            firstItems.Select(item => item.GetProperty("release").GetString()!));

        var cursor = first.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await GetReleasesAsync(api, projectId, $"?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        var secondItems = second.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("1.0.0", Assert.Single(secondItems).GetProperty("release").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task Releases_can_be_filtered_by_environment()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime,
            "env-prod",
            environment: "production",
            release: "prod-release");
        await ManagementApiData.IngestEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddHours(1),
            "env-staging",
            environment: "staging",
            release: "staging-release");

        var staging = await GetReleasesAsync(api, projectId, "?environment=staging");
        var item = Assert.Single(staging.GetProperty("items").EnumerateArray());
        Assert.Equal("staging-release", item.GetProperty("release").GetString());
        Assert.Equal(1, item.GetProperty("event_count").GetInt64());
        Assert.Equal(BaseTime.AddHours(1), item.GetProperty("first_seen").GetDateTimeOffset());

        var all = await GetReleasesAsync(api, projectId);
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=abc")]
    [InlineData("?limit=-1")]
    public async Task Invalid_release_limits_are_rejected(string query)
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "limit", release: "1.0.0");

        using var response = await api.GetAsync($"/api/manage/projects/{projectId}/releases{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ManagementApiData.ReadJsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Malformed_release_cursors_are_rejected()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime, "cursor", release: "1.0.0");

        using var response = await api.GetAsync($"/api/manage/projects/{projectId}/releases?cursor=not-a-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_project_returns_404()
    {
        using var api = new ManagementApiClient(_fixture);

        using var response = await api.GetAsync("/api/manage/projects/999999999/releases");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonElement> GetReleasesAsync(
        ManagementApiClient api,
        long projectId,
        string query = "")
    {
        using var response = await api.GetAsync($"/api/manage/projects/{projectId}/releases{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ManagementApiData.ReadJsonAsync(response);
    }
}
