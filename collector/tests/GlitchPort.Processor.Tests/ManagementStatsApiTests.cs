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

        var buckets = await GetBucketsAsync(api, TestData.NextProjectId(), query: string.Empty);

        Assert.Equal(24, buckets.Count);
        Assert.All(buckets, bucket => Assert.Equal(0, bucket.GetProperty("count").GetInt64()));
        AssertConsecutive(buckets, TimeSpan.FromHours(1));
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

    private static async Task<List<JsonElement>> GetBucketsAsync(
        ManagementApiClient api,
        long projectId,
        string query)
    {
        using var response = await api.GetAsync($"/api/manage/projects/{projectId}/stats{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ManagementApiData.ReadJsonAsync(response);
        return body.EnumerateArray().Select(bucket => bucket.Clone()).ToList();
    }

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
