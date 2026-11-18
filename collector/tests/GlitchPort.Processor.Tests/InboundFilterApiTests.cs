using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class InboundFilterApiTests
{
    private const string ProjectsPath = "/api/manage/projects";

    /// <summary>An id that no project of the fixture can have.</summary>
    private const long MissingId = 987654322;

    private readonly ProcessorFixture _fixture;

    public InboundFilterApiTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Project_detail_and_list_expose_empty_inbound_filters_by_default()
    {
        using var api = new ManagementApiClient(_fixture);
        var projectId = (await CreateProjectAsync(api, "Default Filters"))
            .GetProperty("id")
            .GetInt64();

        using var detailResponse = await api.GetAsync($"{ProjectsPath}/{projectId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

        var detail = await ManagementApiData.ReadJsonAsync(detailResponse);
        var detailFilters = detail.GetProperty("inbound_filters");
        Assert.Empty(Strings(detailFilters, "releases"));
        Assert.Empty(Strings(detailFilters, "environments"));
        Assert.Empty(Strings(detailFilters, "message_patterns"));

        using var listResponse = await api.GetAsync(ProjectsPath);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var item = (await ManagementApiData.ReadJsonAsync(listResponse))
            .EnumerateArray()
            .Single(project => project.GetProperty("id").GetInt64() == projectId)
            .Clone();

        var listFilters = item.GetProperty("inbound_filters");
        Assert.Empty(Strings(listFilters, "releases"));
        Assert.Empty(Strings(listFilters, "environments"));
        Assert.Empty(Strings(listFilters, "message_patterns"));
    }

    [Fact]
    public async Task Patch_replaces_only_the_provided_filter_arrays()
    {
        using var api = new ManagementApiClient(_fixture);
        var projectId = (await CreateProjectAsync(api, "Partial Filters"))
            .GetProperty("id")
            .GetInt64();

        // Items are trimmed and duplicates removed.
        using (var first = await api.PatchAsync(
                   $"{ProjectsPath}/{projectId}",
                   """
                   {"inbound_filters":{"releases":[" 1.2.3 ","2.0.0","1.2.3"],"environments":["prod"]}}
                   """))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            var filters = (await ManagementApiData.ReadJsonAsync(first)).GetProperty("inbound_filters");
            Assert.Equal(["1.2.3", "2.0.0"], Strings(filters, "releases"));
            Assert.Equal(["prod"], Strings(filters, "environments"));
            Assert.Empty(Strings(filters, "message_patterns"));
        }

        // A name-only patch keeps the filters.
        using (var rename = await api.PatchAsync(
                   $"{ProjectsPath}/{projectId}",
                   """{"name":"Renamed Filters"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

            var filters = (await ManagementApiData.ReadJsonAsync(rename)).GetProperty("inbound_filters");
            Assert.Equal(["1.2.3", "2.0.0"], Strings(filters, "releases"));
            Assert.Equal(["prod"], Strings(filters, "environments"));
        }

        // Only message_patterns is replaced; the other arrays keep their values.
        using (var messages = await api.PatchAsync(
                   $"{ProjectsPath}/{projectId}",
                   """{"inbound_filters":{"message_patterns":["*timeout*","*Timeout*"]}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, messages.StatusCode);

            var filters = (await ManagementApiData.ReadJsonAsync(messages)).GetProperty("inbound_filters");
            Assert.Equal(["1.2.3", "2.0.0"], Strings(filters, "releases"));
            Assert.Equal(["prod"], Strings(filters, "environments"));
            Assert.Equal(["*timeout*", "*Timeout*"], Strings(filters, "message_patterns"));
        }

        // An empty array clears just that filter.
        using (var clear = await api.PatchAsync(
                   $"{ProjectsPath}/{projectId}",
                   """{"inbound_filters":{"releases":[]}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

            var filters = (await ManagementApiData.ReadJsonAsync(clear)).GetProperty("inbound_filters");
            Assert.Empty(Strings(filters, "releases"));
            Assert.Equal(["prod"], Strings(filters, "environments"));
            Assert.Equal(["*timeout*", "*Timeout*"], Strings(filters, "message_patterns"));
        }

        // An empty object and an omitted object change nothing.
        using (var empty = await api.PatchAsync(
                   $"{ProjectsPath}/{projectId}",
                   """{"inbound_filters":{}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);

            var filters = (await ManagementApiData.ReadJsonAsync(empty)).GetProperty("inbound_filters");
            Assert.Empty(Strings(filters, "releases"));
            Assert.Equal(["prod"], Strings(filters, "environments"));
            Assert.Equal(["*timeout*", "*Timeout*"], Strings(filters, "message_patterns"));
        }

        using (var detailResponse = await api.GetAsync($"{ProjectsPath}/{projectId}"))
        {
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

            var filters = (await ManagementApiData.ReadJsonAsync(detailResponse))
                .GetProperty("inbound_filters");
            Assert.Empty(Strings(filters, "releases"));
            Assert.Equal(["prod"], Strings(filters, "environments"));
            Assert.Equal(["*timeout*", "*Timeout*"], Strings(filters, "message_patterns"));
        }
    }

    [Fact]
    public async Task Patch_accepts_fifty_items_and_items_of_two_hundred_characters()
    {
        using var api = new ManagementApiClient(_fixture);
        var projectId = (await CreateProjectAsync(api, "Limit Filters"))
            .GetProperty("id")
            .GetInt64();

        // Every item is exactly 200 characters long.
        var items = Enumerable
            .Range(0, 50)
            .Select(index => $"pattern-{index:D2}-" + new string('x', 189))
            .ToList();

        var body = new JsonObject
        {
            ["inbound_filters"] = new JsonObject
            {
                ["releases"] = new JsonArray(items.Select(item => (JsonNode)item).ToArray()),
            },
        };

        using var response = await api.PatchAsync($"{ProjectsPath}/{projectId}", body.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var filters = (await ManagementApiData.ReadJsonAsync(response)).GetProperty("inbound_filters");
        Assert.Equal(items, Strings(filters, "releases"));
    }

    [Theory]
    [InlineData("""{"inbound_filters":null}""")]
    [InlineData("""{"inbound_filters":[]}""")]
    [InlineData("""{"inbound_filters":"releases"}""")]
    [InlineData("""{"inbound_filters":7}""")]
    [InlineData("""{"inbound_filters":{"releases":"1.2.3"}}""")]
    [InlineData("""{"inbound_filters":{"releases":null}}""")]
    [InlineData("""{"inbound_filters":{"releases":7}}""")]
    [InlineData("""{"inbound_filters":{"releases":[1]}}""")]
    [InlineData("""{"inbound_filters":{"releases":[null]}}""")]
    [InlineData("""{"inbound_filters":{"releases":[""]}}""")]
    [InlineData("""{"inbound_filters":{"releases":["   "]}}""")]
    [InlineData("""{"inbound_filters":{"environments":[true]}}""")]
    [InlineData("""{"inbound_filters":{"message_patterns":[{"pattern":"boom"}]}}""")]
    public async Task Patch_rejects_invalid_inbound_filters(string json)
    {
        using var api = new ManagementApiClient(_fixture);
        var projectId = (await CreateProjectAsync(api, "Invalid Filters"))
            .GetProperty("id")
            .GetInt64();

        using (var seed = await api.PatchAsync(
                   $"{ProjectsPath}/{projectId}",
                   """{"inbound_filters":{"releases":["1.2.3"]}}"""))
        {
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        }

        using var response = await api.PatchAsync($"{ProjectsPath}/{projectId}", json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await ManagementApiData.ReadJsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("error").GetString()));

        // The rejected request left the stored filters untouched.
        using var detailResponse = await api.GetAsync($"{ProjectsPath}/{projectId}");
        var filters = (await ManagementApiData.ReadJsonAsync(detailResponse))
            .GetProperty("inbound_filters");
        Assert.Equal(["1.2.3"], Strings(filters, "releases"));
        Assert.Empty(Strings(filters, "environments"));
        Assert.Empty(Strings(filters, "message_patterns"));
    }

    [Fact]
    public async Task Patch_rejects_more_than_fifty_items()
    {
        using var api = new ManagementApiClient(_fixture);
        var projectId = (await CreateProjectAsync(api, "Too Many Filters"))
            .GetProperty("id")
            .GetInt64();

        var items = Enumerable
            .Range(0, 51)
            .Select(index => (JsonNode)$"pattern-{index}")
            .ToArray();

        var body = new JsonObject
        {
            ["inbound_filters"] = new JsonObject
            {
                ["message_patterns"] = new JsonArray(items),
            },
        };

        using var response = await api.PatchAsync($"{ProjectsPath}/{projectId}", body.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_rejects_items_longer_than_two_hundred_characters()
    {
        using var api = new ManagementApiClient(_fixture);
        var projectId = (await CreateProjectAsync(api, "Long Filter"))
            .GetProperty("id")
            .GetInt64();

        var item = new string('x', 201);
        var body = new JsonObject
        {
            ["inbound_filters"] = new JsonObject
            {
                ["environments"] = new JsonArray(item),
            },
        };

        using var response = await api.PatchAsync($"{ProjectsPath}/{projectId}", body.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_of_an_unknown_project_returns_not_found()
    {
        using var api = new ManagementApiClient(_fixture);

        using var response = await api.PatchAsync(
            $"{ProjectsPath}/{MissingId}",
            """{"inbound_filters":{"releases":["1.2.3"]}}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static List<string> Strings(JsonElement element, string name) =>
        element.GetProperty(name)
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToList();

    private static async Task<JsonElement> CreateProjectAsync(ManagementApiClient api, string name)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["slug"] = $"filters-{Guid.NewGuid():N}",
        };

        using var response = await api.PostAsync(ProjectsPath, body.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ManagementApiData.ReadJsonAsync(response);
    }
}
