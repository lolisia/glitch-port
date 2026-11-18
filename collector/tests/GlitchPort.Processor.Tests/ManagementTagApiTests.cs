using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ManagementTagApiTests
{
    private const string SampleLimitConfigurationKey = "Processor:Management:TagSampleLimit";

    private static readonly DateTimeOffset BaseTime = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] FixedKeys =
    [
        "release",
        "environment",
        "platform",
        "os.name",
        "device.model",
        "gpu.name",
        "gpu.api",
        "runtime.version",
    ];

    private readonly ProcessorFixture _fixture;

    public ManagementTagApiTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Tags_report_fixed_keys_and_the_payload_tags()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await IngestAsync(
            api,
            projectId,
            BaseTime,
            release: "1.2.3",
            environment: "production",
            userId: "user-1",
            contexts: new JsonObject
            {
                ["os"] = new JsonObject { ["name"] = "Windows 11" },
                ["device"] = new JsonObject { ["model"] = "Pixel 9" },
                ["gpu"] = new JsonObject { ["name"] = "RTX 4090", ["api"] = "DirectX 12" },
                ["runtime"] = new JsonObject { ["version"] = ".NET 8" },
            },
            tags: new JsonObject { ["level"] = "high", ["map"] = "dust", ["mode"] = "ranked" });
        await IngestAsync(
            api,
            projectId,
            BaseTime.AddMinutes(1),
            release: "1.2.4",
            environment: "staging",
            userId: "user-2",
            contexts: new JsonObject
            {
                ["os"] = new JsonObject { ["name"] = "Windows 11" },
                ["device"] = new JsonObject { ["model"] = "Pixel 9" },
            },
            tags: new JsonObject { ["level"] = "low", ["map"] = "dust", ["empty"] = string.Empty });
        await IngestAsync(
            api,
            projectId,
            BaseTime.AddMinutes(2),
            release: "1.2.3",
            environment: "production",
            userId: "user-2",
            contexts: new JsonObject { ["os"] = new JsonObject { ["name"] = "Linux" } },
            tags: new JsonObject { ["level"] = "high", ["extra"] = "x" });

        var issueId = await SingleIssueIdAsync(api, projectId);
        var root = await GetTagsAsync(api, issueId);

        Assert.Equal(issueId, root.GetProperty("issue_id").GetInt64());
        Assert.Equal(3, root.GetProperty("sample_size").GetInt32());
        Assert.Equal(2, root.GetProperty("user_count").GetInt64());

        var keys = root.GetProperty("tags").EnumerateArray()
            .Select(facet => facet.GetProperty("key").GetString()!)
            .ToList();

        // Fixed keys keep their documented order; payload tag facets follow in
        // frequency order.
        Assert.Equal(
            FixedKeys.Concat(["tags.level", "tags.map", "tags.extra", "tags.mode"]),
            keys);

        AssertFacet(Facet(root, "release"), 3, 2, ("1.2.3", 2), ("1.2.4", 1));
        AssertFacet(Facet(root, "environment"), 3, 2, ("production", 2), ("staging", 1));
        AssertFacet(Facet(root, "platform"), 3, 1, ("unity", 3));
        AssertFacet(Facet(root, "os.name"), 3, 2, ("Windows 11", 2), ("Linux", 1));
        AssertFacet(Facet(root, "device.model"), 2, 1, ("Pixel 9", 2));
        AssertFacet(Facet(root, "gpu.name"), 1, 1, ("RTX 4090", 1));
        AssertFacet(Facet(root, "gpu.api"), 1, 1, ("DirectX 12", 1));
        AssertFacet(Facet(root, "runtime.version"), 1, 1, (".NET 8", 1));
        AssertFacet(Facet(root, "tags.level"), 3, 2, ("high", 2), ("low", 1));
        AssertFacet(Facet(root, "tags.map"), 2, 1, ("dust", 2));
        AssertFacet(Facet(root, "tags.extra"), 1, 1, ("x", 1));
        AssertFacet(Facet(root, "tags.mode"), 1, 1, ("ranked", 1));

        // A tag key that only carries an empty value does not get a facet.
        Assert.DoesNotContain(
            root.GetProperty("tags").EnumerateArray(),
            facet => facet.GetProperty("key").GetString() == "tags.empty");
    }

    [Fact]
    public async Task Fixed_tag_keys_are_always_present()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await IngestAsync(api, projectId, BaseTime);

        var issueId = await SingleIssueIdAsync(api, projectId);
        var root = await GetTagsAsync(api, issueId);

        Assert.Equal(1, root.GetProperty("sample_size").GetInt32());
        Assert.Equal(0, root.GetProperty("user_count").GetInt64());

        var facets = root.GetProperty("tags").EnumerateArray().ToList();
        Assert.Equal(FixedKeys, facets.Select(facet => facet.GetProperty("key").GetString()!));

        AssertFacet(Facet(root, "release"), 0, 0);
        AssertFacet(Facet(root, "environment"), 0, 0);
        AssertFacet(Facet(root, "platform"), 1, 1, ("unity", 1));
        AssertFacet(Facet(root, "os.name"), 0, 0);
        AssertFacet(Facet(root, "runtime.version"), 0, 0);
    }

    [Fact]
    public async Task Empty_values_are_excluded_from_the_counts()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await IngestAsync(
            api,
            projectId,
            BaseTime,
            contexts: new JsonObject { ["os"] = new JsonObject { ["name"] = string.Empty } },
            tags: new JsonObject { ["blank"] = string.Empty, ["filled"] = "yes" });
        await IngestAsync(
            api,
            projectId,
            BaseTime.AddMinutes(1),
            tags: new JsonObject { ["filled"] = "yes", ["nullish"] = null });

        var issueId = await SingleIssueIdAsync(api, projectId);
        var root = await GetTagsAsync(api, issueId);

        AssertFacet(Facet(root, "os.name"), 0, 0);
        AssertFacet(Facet(root, "tags.filled"), 2, 1, ("yes", 2));
        Assert.DoesNotContain(
            root.GetProperty("tags").EnumerateArray(),
            facet => facet.GetProperty("key").GetString() is "tags.blank" or "tags.nullish");
    }

    [Fact]
    public async Task Top_values_are_ordered_by_count_and_value_and_capped_by_limit()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        for (var index = 0; index < 3; index++)
        {
            await IngestAsync(api, projectId, BaseTime.AddMinutes(index), release: "beta");
        }

        for (var index = 0; index < 2; index++)
        {
            await IngestAsync(api, projectId, BaseTime.AddMinutes(3 + index), release: "alpha");
        }

        await IngestAsync(api, projectId, BaseTime.AddMinutes(5), release: "charlie");
        await IngestAsync(api, projectId, BaseTime.AddMinutes(6), release: "delta");

        var issueId = await SingleIssueIdAsync(api, projectId);

        var full = await GetTagsAsync(api, issueId);
        AssertFacet(
            Facet(full, "release"),
            7,
            4,
            ("beta", 3),
            ("alpha", 2),
            ("charlie", 1),
            ("delta", 1));

        var capped = await GetTagsAsync(api, issueId, "?limit=2");
        var facet = Facet(capped, "release");
        Assert.Equal(7, facet.GetProperty("total").GetInt64());
        Assert.Equal(4, facet.GetProperty("distinct").GetInt64());
        AssertTop(facet, ("beta", 3), ("alpha", 2));

        using var largest = await api.GetAsync($"/api/manage/issues/{issueId}/tags?limit=25");
        Assert.Equal(HttpStatusCode.OK, largest.StatusCode);
    }

    [Fact]
    public async Task Top_values_default_to_ten_and_accept_up_to_twenty_five()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        for (var index = 1; index <= 12; index++)
        {
            await IngestAsync(api, projectId, BaseTime.AddMinutes(index), release: $"v{index:00}");
        }

        var issueId = await SingleIssueIdAsync(api, projectId);

        var defaults = await GetTagsAsync(api, issueId);
        Assert.Equal(10, Facet(defaults, "release").GetProperty("top").GetArrayLength());

        var widened = await GetTagsAsync(api, issueId, "?limit=25");
        var top = Facet(widened, "release").GetProperty("top").EnumerateArray()
            .Select(item => item.GetProperty("value").GetString()!)
            .ToList();

        Assert.Equal(12, top.Count);
        Assert.Equal(Enumerable.Range(1, 12).Select(index => $"v{index:00}"), top);
    }

    [Fact]
    public async Task Only_the_twenty_most_frequent_payload_tag_keys_get_facets()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        var tags = new JsonObject();
        for (var index = 1; index <= 25; index++)
        {
            tags[$"k{index:00}"] = "v";
        }

        await IngestAsync(api, projectId, BaseTime, tags: tags);

        var issueId = await SingleIssueIdAsync(api, projectId);
        var root = await GetTagsAsync(api, issueId);

        var keys = root.GetProperty("tags").EnumerateArray()
            .Select(facet => facet.GetProperty("key").GetString()!)
            .ToList();

        Assert.Equal(FixedKeys.Length + 20, keys.Count);
        for (var index = 1; index <= 20; index++)
        {
            Assert.Contains($"tags.k{index:00}", keys);
        }

        for (var index = 21; index <= 25; index++)
        {
            Assert.DoesNotContain($"tags.k{index:00}", keys);
        }
    }

    [Fact]
    public async Task Only_the_newest_events_are_sampled()
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await IngestAsync(api, projectId, BaseTime, release: "r1", userId: "user-1");
        await IngestAsync(api, projectId, BaseTime.AddMinutes(1), release: "r2", userId: "user-2");
        await IngestAsync(api, projectId, BaseTime.AddMinutes(2), release: "r3", userId: "user-3");

        var issueId = await SingleIssueIdAsync(api, projectId);

        // The sample limit is read at startup, so this host sees a small one.
        using var factory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SampleLimitConfigurationKey] = "2",
                });
            });
        });

        using var client = factory.CreateClient();
        using var request = TestData.CreateAuthorizedGet($"/api/manage/issues/{issueId}/tags");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await ManagementApiData.ReadJsonAsync(response);

        Assert.Equal(2, root.GetProperty("sample_size").GetInt32());
        Assert.Equal(2, root.GetProperty("user_count").GetInt64());
        AssertFacet(Facet(root, "release"), 2, 2, ("r2", 1), ("r3", 1));
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=26")]
    [InlineData("?limit=abc")]
    [InlineData("?limit=-1")]
    public async Task Invalid_tag_limits_are_rejected(string query)
    {
        var projectId = TestData.NextProjectId();
        using var api = new ManagementApiClient(_fixture);

        await IngestAsync(api, projectId, BaseTime);
        var issueId = await SingleIssueIdAsync(api, projectId);

        using var response = await api.GetAsync($"/api/manage/issues/{issueId}/tags{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ManagementApiData.ReadJsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Unknown_issue_returns_404()
    {
        using var api = new ManagementApiClient(_fixture);

        using var response = await api.GetAsync("/api/manage/issues/999999999/tags");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task IngestAsync(
        ManagementApiClient api,
        long projectId,
        DateTimeOffset timestamp,
        string? release = null,
        string? environment = null,
        string platform = "unity",
        string? userId = null,
        JsonObject? contexts = null,
        JsonObject? tags = null)
    {
        var payload = new JsonObject
        {
            ["level"] = "error",
            ["platform"] = platform,
            ["timestamp"] = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["fingerprint"] = new JsonArray(JsonValue.Create($"{projectId}-tag-fingerprint")),
        };

        if (release is not null)
        {
            payload["release"] = release;
        }

        if (environment is not null)
        {
            payload["environment"] = environment;
        }

        if (userId is not null)
        {
            payload["user"] = new JsonObject { ["id"] = userId };
        }

        if (contexts is not null)
        {
            payload["contexts"] = contexts;
        }

        if (tags is not null)
        {
            payload["tags"] = tags;
        }

        var envelope = TestData.CreatePayloadEnvelope(
            Guid.NewGuid(),
            projectId,
            timestamp,
            payload.ToJsonString());

        using var response = await TestData.PostEnvelopesAsync(
            api.Raw,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await TestData.ReadStoredAsync(response));
    }

    private static async Task<long> SingleIssueIdAsync(ManagementApiClient api, long projectId)
    {
        var page = await ManagementApiData.GetIssuesAsync(api, projectId);
        return Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64();
    }

    private static async Task<JsonElement> GetTagsAsync(
        ManagementApiClient api,
        long issueId,
        string query = "")
    {
        using var response = await api.GetAsync($"/api/manage/issues/{issueId}/tags{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ManagementApiData.ReadJsonAsync(response);
    }

    private static JsonElement Facet(JsonElement root, string key) =>
        root.GetProperty("tags").EnumerateArray()
            .Single(facet => facet.GetProperty("key").GetString() == key);

    private static void AssertFacet(
        JsonElement facet,
        long total,
        long distinct,
        params (string Value, long Count)[] top)
    {
        Assert.Equal(total, facet.GetProperty("total").GetInt64());
        Assert.Equal(distinct, facet.GetProperty("distinct").GetInt64());
        AssertTop(facet, top);
    }

    private static void AssertTop(JsonElement facet, params (string Value, long Count)[] expected)
    {
        var actual = facet.GetProperty("top").EnumerateArray()
            .Select(item => (item.GetProperty("value").GetString()!, item.GetProperty("count").GetInt64()))
            .ToList();

        Assert.Equal(expected, actual);
    }
}
