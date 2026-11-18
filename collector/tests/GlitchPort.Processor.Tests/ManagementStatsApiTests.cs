using System.Globalization;
using System.Net;
using System.Text.Json;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ManagementStatsApiTests
{
    private readonly ProcessorFixture _fixture;

    public ManagementStatsApiTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Hourly_stats_return_24_buckets_including_empty_ones()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        var currentHour = CurrentHour();
        var twoHoursAgo = currentHour.AddHours(-2);
        var fiveHoursAgo = currentHour.AddHours(-5);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, fiveHoursAgo, "stats-hourly-1");
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, twoHoursAgo, "stats-hourly-2");
        await ManagementApiData.IngestEventAsync(api.Raw, projectId, twoHoursAgo.AddMinutes(20), "stats-hourly-2");

        var buckets = await GetBucketsAsync(api, projectId, "?period=24h");

        Assert.Equal(24, buckets.Count);
        AssertConsecutive(buckets, TimeSpan.FromHours(1));
        Assert.Equal(3, buckets.Sum(bucket => bucket.GetProperty("count").GetInt64()));
        Assert.Equal(22, buckets.Count(bucket => bucket.GetProperty("count").GetInt64() == 0));
        Assert.Equal(1, CountAt(buckets, fiveHoursAgo));
        Assert.Equal(2, CountAt(buckets, twoHoursAgo));
    }

    [Fact]
    public async Task Daily_stats_return_14_buckets_including_empty_ones()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        var today = CurrentDay();
        var threeDaysAgo = today.AddDays(-3).AddHours(12);

        await ManagementApiData.IngestEventAsync(api.Raw, projectId, threeDaysAgo, "stats-daily");

        var buckets = await GetBucketsAsync(api, projectId, "?period=14d");

        Assert.Equal(14, buckets.Count);
        AssertConsecutive(buckets, TimeSpan.FromDays(1));
        Assert.Equal(1, buckets.Sum(bucket => bucket.GetProperty("count").GetInt64()));
        Assert.Equal(13, buckets.Count(bucket => bucket.GetProperty("count").GetInt64() == 0));
        Assert.Equal(1, CountAt(buckets, today.AddDays(-3)));
    }

    [Fact]
    public async Task Stats_default_to_24_hours_and_include_empty_projects()
    {
        using var api = new ManagementApiClient(_fixture);

        var stats = await GetStatsAsync(api, TestData.NextProjectId(), query: string.Empty);
        var buckets = Buckets(stats);

        Assert.Equal(24, buckets.Count);
        Assert.All(buckets, bucket => Assert.Equal(0, bucket.GetProperty("count").GetInt64()));
        AssertConsecutive(buckets, TimeSpan.FromHours(1));
        Assert.Empty(stats.GetProperty("by_platform").EnumerateArray());
    }

    [Fact]
    public async Task Stats_aggregate_issue_and_event_totals_per_platform()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        // Two unity issues with 4 + 2 events, one dotnet issue with 6 events
        // and one godot issue with 1 event. Unity and dotnet tie on the total,
        // so the platform name breaks the tie.
        await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            projectId,
            "unity issue one",
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(-2),
            eventCount: 4,
            platform: "unity");
        await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            projectId,
            "unity issue two",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(-1),
            eventCount: 2,
            platform: "unity");
        await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            projectId,
            "dotnet issue",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(-1),
            eventCount: 6,
            platform: "dotnet");
        await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            projectId,
            "godot issue",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            eventCount: 1,
            platform: "godot");

        var stats = await GetStatsAsync(api, projectId, "?period=14d");
        var byPlatform = stats.GetProperty("by_platform").EnumerateArray().Select(item => item.Clone()).ToList();

        Assert.Equal(
            ["dotnet", "unity", "godot"],
            byPlatform.Select(item => item.GetProperty("platform").GetString()));
        Assert.Equal(
            [1L, 2L, 1L],
            byPlatform.Select(item => item.GetProperty("issue_count").GetInt64()));
        Assert.Equal(
            [6L, 6L, 1L],
            byPlatform.Select(item => item.GetProperty("event_count").GetInt64()));

        // by_platform does not depend on the period of the buckets.
        var hourly = await GetStatsAsync(api, projectId, "?period=24h");
        Assert.Equal(
            ["dotnet", "unity", "godot"],
            hourly.GetProperty("by_platform").EnumerateArray()
                .Select(item => item.GetProperty("platform").GetString()));
    }

    [Theory]
    [InlineData("7d")]
    [InlineData("1h")]
    [InlineData("24")]
    public async Task Invalid_periods_are_rejected(string period)
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.GetAsync(
            $"/api/manage/projects/{TestData.NextProjectId()}/stats?period={period}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await ManagementApiData.ReadJsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    private static async Task<JsonElement> GetStatsAsync(
        ManagementApiClient api,
        long projectId,
        string query)
    {
        using var response = await api.GetAsync($"/api/manage/projects/{projectId}/stats{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await ManagementApiData.ReadJsonAsync(response);
    }

    private static async Task<List<JsonElement>> GetBucketsAsync(
        ManagementApiClient api,
        long projectId,
        string query) =>
        Buckets(await GetStatsAsync(api, projectId, query));

    private static List<JsonElement> Buckets(JsonElement stats) =>
        stats.GetProperty("buckets").EnumerateArray().Select(bucket => bucket.Clone()).ToList();

    private static void AssertConsecutive(List<JsonElement> buckets, TimeSpan step)
    {
        for (var index = 1; index < buckets.Count; index++)
        {
            var previous = ReadBucket(buckets[index - 1]);
            var current = ReadBucket(buckets[index]);
            Assert.Equal(previous.Add(step), current);
        }
    }

    private static long CountAt(List<JsonElement> buckets, DateTimeOffset expectedBucket) =>
        buckets
            .Where(bucket => ReadBucket(bucket) == expectedBucket.ToUniversalTime())
            .Select(bucket => bucket.GetProperty("count").GetInt64())
            .Single();

    private static DateTimeOffset ReadBucket(JsonElement bucket) =>
        DateTimeOffset.Parse(
            bucket.GetProperty("bucket").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    private static DateTimeOffset CurrentHour()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset CurrentDay()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
    }
}
