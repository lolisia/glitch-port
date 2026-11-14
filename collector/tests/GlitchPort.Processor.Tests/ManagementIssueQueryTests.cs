using System.Net;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ManagementIssueQueryTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ProcessorFixture _fixture;

    public ManagementIssueQueryTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Issues_default_to_unresolved_and_accept_all()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestMessageEventAsync(
            api.Raw,
            projectId,
            BaseTime,
            "Payment service timed out",
            level: "error");
        await ManagementApiData.IngestMessageEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddMinutes(1),
            "Database connection lost",
            level: "warning");

        var all = await ManagementApiData.GetIssuesAsync(api, projectId);
        var timedOutId = ManagementApiData.IssueIdByTitle(all, "Payment service timed out");

        using (var patch = await api.PatchAsync(
                   $"/api/manage/issues/{timedOutId}",
                   """{"status":"resolved"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        var defaultPage = await ManagementApiData.GetIssuesAsync(api, projectId, query: string.Empty);
        var unresolvedPage = await ManagementApiData.GetIssuesAsync(api, projectId, query: "?status=unresolved");
        var resolvedPage = await ManagementApiData.GetIssuesAsync(api, projectId, query: "?status=resolved");
        var everyStatusPage = await ManagementApiData.GetIssuesAsync(api, projectId, query: "?status=all");

        Assert.Equal("Database connection lost", SingleTitle(defaultPage));
        Assert.Equal("Database connection lost", SingleTitle(unresolvedPage));
        Assert.Equal("Payment service timed out", SingleTitle(resolvedPage));
        Assert.Equal(2, everyStatusPage.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Issues_can_be_filtered_by_level_environment_release_label_and_title()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await ManagementApiData.IngestMessageEventAsync(
            api.Raw,
            projectId,
            BaseTime,
            "Payment service timed out",
            level: "error",
            environment: "production",
            release: "star-raiders@1.0.0");
        await ManagementApiData.IngestMessageEventAsync(
            api.Raw,
            projectId,
            BaseTime.AddMinutes(1),
            "Database connection lost",
            level: "warning",
            environment: "staging",
            release: "star-raiders@1.1.0");

        var all = await ManagementApiData.GetIssuesAsync(api, projectId);
        var paymentId = ManagementApiData.IssueIdByTitle(all, "Payment service timed out");

        using (var patch = await api.PatchAsync($"/api/manage/issues/{paymentId}", """{"labels":["billing"]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        Assert.Equal("Payment service timed out", SingleTitle(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&level=error")));
        Assert.Equal("Database connection lost", SingleTitle(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&level=warning")));
        Assert.Equal("Payment service timed out", SingleTitle(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&environment=production")));
        Assert.Equal("Database connection lost", SingleTitle(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&environment=staging")));
        Assert.Equal("Payment service timed out", SingleTitle(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&release=star-raiders@1.0.0")));
        Assert.Equal("Database connection lost", SingleTitle(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&release=star-raiders@1.1.0")));
        Assert.Equal("Payment service timed out", SingleTitle(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&label=billing")));
        Assert.Equal("Database connection lost", SingleTitle(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&q=database")));
        Assert.Empty(Items(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&q=%25connection%25")));
        Assert.Empty(Items(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&q=%5C")));
        Assert.Equal("Payment service timed out", SingleTitle(
            await ManagementApiData.GetIssuesAsync(
                api,
                projectId,
                "?status=all&level=error&environment=production&q=payment")));
        Assert.Empty(Items(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&label=does-not-exist")));
        Assert.Empty(Items(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=all&q=does-not-exist")));
        Assert.Empty(Items(
            await ManagementApiData.GetIssuesAsync(api, projectId, "?status=resolved&q=payment")));
    }

    [Theory]
    [InlineData("sort=created")]
    [InlineData("status=done")]
    [InlineData("level=critical")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=not-a-number")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=")]
    public async Task Invalid_issue_query_values_are_rejected(string query)
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.GetAsync($"/api/manage/projects/{TestData.NextProjectId()}/issues?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await ManagementApiData.ReadJsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Issues_are_sorted_and_paginated_by_last_seen_first_seen_and_count()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        // one event: first_seen == last_seen == BaseTime - 1h
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddHours(-1), "fp-one", release: "release-3");
        // two events
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddHours(-3), "fp-two", release: "release-2");
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddMinutes(-150), "fp-two", release: "release-2");
        // three events; the oldest first_seen but the newest last_seen
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddHours(-5), "fp-three", release: "release-1");
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddMinutes(-60), "fp-three", release: "release-1");
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, BaseTime.AddMinutes(-30), "fp-three", release: "release-1");

        var byLastSeen = await ReadPagesAsync(api, projectId, "?status=all&sort=last_seen&limit=1");
        var byFirstSeen = await ReadPagesAsync(api, projectId, "?status=all&sort=first_seen&limit=1");
        var byCount = await ReadPagesAsync(api, projectId, "?status=all&sort=count&limit=1");

        Assert.Equal(new[] { "release-1", "release-3", "release-2" }, byLastSeen.Select(Release));
        Assert.Equal(new[] { "release-3", "release-2", "release-1" }, byFirstSeen.Select(Release));
        Assert.Equal(new[] { "release-1", "release-2", "release-3" }, byCount.Select(Release));

        using var firstPage = await api.GetAsync(
            $"/api/manage/projects/{projectId}/issues?status=all&sort=count&limit=2");
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);

        var firstPageBody = await ManagementApiData.ReadJsonAsync(firstPage);
        Assert.Equal(2, firstPageBody.GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(firstPageBody.GetProperty("next_cursor").GetString()));

        var secondPage = await ReadPageAsync(
            api,
            projectId,
            $"?status=all&sort=count&limit=2&cursor={Uri.EscapeDataString(firstPageBody.GetProperty("next_cursor").GetString()!)}");
        Assert.Single(Items(secondPage).ToList());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("next_cursor").ValueKind);

        // A cursor is only valid for the sort that produced it.
        using var lastSeenPage = await api.GetAsync(
            $"/api/manage/projects/{projectId}/issues?status=all&sort=last_seen&limit=1");
        var lastSeenCursor = (await ManagementApiData.ReadJsonAsync(lastSeenPage))
            .GetProperty("next_cursor")
            .GetString();

        using var mismatched = await api.GetAsync(
            $"/api/manage/projects/{projectId}/issues?status=all&sort=count&cursor={Uri.EscapeDataString(lastSeenCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
    }

    [Fact]
    public async Task Issue_list_honours_the_default_and_maximum_page_sizes()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
             insert into issues (project_id, fingerprint, title, level, platform, first_seen, last_seen)
             select {projectId},
                    'synthetic-' || g,
                    'Synthetic issue ' || g,
                    'error',
                    'unity',
                    now() - (g || ' minutes')::interval,
                    now() - (g || ' minutes')::interval
             from generate_series(1, 26) as g
             """);

        var defaultPage = await ManagementApiData.GetIssuesAsync(api, projectId, query: "?status=all");
        var fullPage = await ManagementApiData.GetIssuesAsync(api, projectId, query: "?status=all&limit=100");
        var twoItems = await ManagementApiData.GetIssuesAsync(api, projectId, query: "?status=all&limit=2");

        Assert.Equal(25, defaultPage.GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(defaultPage.GetProperty("next_cursor").GetString()));
        Assert.Equal(26, fullPage.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, fullPage.GetProperty("next_cursor").ValueKind);
        Assert.Equal(2, twoItems.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task An_empty_project_returns_an_empty_page()
    {
        using var api = new ManagementApiClient(_fixture);
        var page = await ManagementApiData.GetIssuesAsync(api, TestData.NextProjectId(), query: string.Empty);

        Assert.Empty(Items(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next_cursor").ValueKind);
    }

    private static string SingleTitle(JsonElement page)
    {
        var items = Items(page).ToList();
        return Assert.Single(items).GetProperty("title").GetString()!;
    }

    private static IEnumerable<JsonElement> Items(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.Clone());

    private static string Release(JsonElement item) => item.GetProperty("last_release").GetString()!;

    private static async Task<JsonElement> ReadPageAsync(
        ManagementApiClient api,
        long projectId,
        string query)
    {
        using var response = await api.GetAsync($"/api/manage/projects/{projectId}/issues{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ManagementApiData.ReadJsonAsync(response);
    }

    private static async Task<List<JsonElement>> ReadPagesAsync(
        ManagementApiClient api,
        long projectId,
        string query)
    {
        var items = new List<JsonElement>();
        string? cursor = null;

        for (var page = 0; page < 20; page++)
        {
            var suffix = cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
            var body = await ReadPageAsync(api, projectId, query + suffix);
            items.AddRange(Items(body));

            var next = body.GetProperty("next_cursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();

            if (cursor is null)
            {
                return items;
            }
        }

        throw new InvalidOperationException("The issue list did not finish paging.");
    }
}
