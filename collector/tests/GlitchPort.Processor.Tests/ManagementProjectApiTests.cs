using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ManagementProjectApiTests
{
    private const string ProjectsPath = "/api/manage/projects";

    /// <summary>An id that no project or key of the fixture can have.</summary>
    private const long MissingId = 987654321;

    private readonly ProcessorFixture _fixture;

    public ManagementProjectApiTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Project_list_returns_unresolved_issue_and_active_key_counts_ordered_by_id()
    {
        using var api = new ManagementApiClient(_fixture);

        var first = await CreateProjectAsync(api, "List Alpha", UniqueSlug("list-alpha"));
        var second = await CreateProjectAsync(api, "List Beta", UniqueSlug("list-beta"));

        var firstId = first.GetProperty("id").GetInt64();
        var secondId = second.GetProperty("id").GetInt64();

        await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            firstId,
            "resolved unity issue",
            DateTimeOffset.UtcNow.AddMinutes(-4),
            DateTimeOffset.UtcNow.AddMinutes(-4),
            status: "resolved",
            eventCount: 5,
            platform: "unity");
        await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            firstId,
            "unresolved unity issue",
            DateTimeOffset.UtcNow.AddMinutes(-3),
            DateTimeOffset.UtcNow.AddMinutes(-3),
            eventCount: 2,
            platform: "unity");
        await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            firstId,
            "dotnet issue",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-2),
            eventCount: 4,
            platform: "dotnet");
        await ManagementApiData.InsertIssueAsync(
            _fixture.ConnectionString,
            firstId,
            "godot issue",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            eventCount: 2,
            platform: "godot");

        var temporaryKey = await CreateKeyAsync(api, firstId, "temporary", rateLimit: null);
        using (var revoke = await api.DeleteAsync($"/api/manage/keys/{temporaryKey.GetProperty("id").GetInt64()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using var response = await api.GetAsync(ProjectsPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = (await ManagementApiData.ReadJsonAsync(response))
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToList();

        var ids = list.Select(item => item.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(ids.OrderBy(id => id), ids);

        var firstItem = list.Single(item => item.GetProperty("id").GetInt64() == firstId);
        Assert.Equal("List Alpha", firstItem.GetProperty("name").GetString());
        Assert.False(firstItem.TryGetProperty("platform", out _));
        Assert.Equal(
            ["unity", "dotnet", "godot"],
            Platforms(firstItem));
        Assert.Equal(3, firstItem.GetProperty("issue_count").GetInt64());
        Assert.Equal(1, firstItem.GetProperty("key_count").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(firstItem.GetProperty("created_at").GetString()));

        var secondItem = list.Single(item => item.GetProperty("id").GetInt64() == secondId);
        Assert.False(secondItem.TryGetProperty("platform", out _));
        Assert.Empty(Platforms(secondItem));
        Assert.Equal(0, secondItem.GetProperty("issue_count").GetInt64());
        Assert.Equal(1, secondItem.GetProperty("key_count").GetInt64());
    }

    [Fact]
    public async Task Project_create_derives_the_slug_and_returns_a_dsn_from_the_configured_relay_url()
    {
        using var api = new ManagementApiClient(_fixture);

        using var response = await api.PostAsync(
            ProjectsPath,
            """{"name":"Nightly Build!! v2"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await ManagementApiData.ReadJsonAsync(response);
        var id = created.GetProperty("id").GetInt64();
        var slug = created.GetProperty("slug").GetString();

        Assert.Equal("Nightly Build!! v2", created.GetProperty("name").GetString());
        Assert.Equal("nightly-build-v2", slug);
        Assert.False(created.TryGetProperty("platform", out _));
        Assert.Empty(Platforms(created));
        Assert.True(DateTimeOffset.TryParse(created.GetProperty("created_at").GetString(), out _));

        var keys = await GetKeysAsync(api, id);
        var key = Assert.Single(keys);
        var publicKey = key.GetProperty("public_key").GetString()!;

        Assert.Matches("^[0-9a-f]{32}$", publicKey);
        Assert.Equal(
            $"{ProcessorApplicationFactory.DefaultPublicRelayUrl.Replace("://", $"://{publicKey}@", StringComparison.Ordinal)}/{id}",
            created.GetProperty("dsn").GetString());
        Assert.Equal(created.GetProperty("dsn").GetString(), key.GetProperty("dsn").GetString());

        using var detailResponse = await api.GetAsync($"{ProjectsPath}/{id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

        var detail = await ManagementApiData.ReadJsonAsync(detailResponse);
        Assert.Equal("nightly-build-v2", detail.GetProperty("slug").GetString());
        Assert.Equal(0, detail.GetProperty("issue_count").GetInt64());
        Assert.Equal(1, detail.GetProperty("key_count").GetInt64());
        Assert.Equal(created.GetProperty("dsn").GetString(), detail.GetProperty("dsn").GetString());
        Assert.False(detail.TryGetProperty("platform", out _));
        Assert.Empty(Platforms(detail));
    }

    [Fact]
    public async Task Project_create_uses_an_explicit_slug_and_rejects_duplicates()
    {
        using var api = new ManagementApiClient(_fixture);

        var slug = UniqueSlug("star-raiders");
        var first = await CreateProjectAsync(api, "Star Raiders", slug);
        Assert.Equal(slug, first.GetProperty("slug").GetString());

        using var duplicate = await api.PostAsync(
            ProjectsPath,
            $$"""{"name":"Another project","slug":"{{slug}}"}""");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var error = await ManagementApiData.ReadJsonAsync(duplicate);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Project_create_falls_back_to_project_when_the_name_has_no_slug_characters()
    {
        using var api = new ManagementApiClient(_fixture);

        var created = await CreateProjectAsync(api, "!!!", slug: null);

        Assert.Equal("!!!", created.GetProperty("name").GetString());
        Assert.Equal("project", created.GetProperty("slug").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":"   "}""")]
    [InlineData("""{"name":"Valid","slug":""}""")]
    [InlineData("""{"name":"Valid","slug":"Bad Slug"}""")]
    [InlineData("""{"name":"Valid","slug":"-leading"}""")]
    [InlineData("""{"name":"Valid","slug":"trailing-"}""")]
    [InlineData("""{"name":"Valid","slug":"double--hyphen"}""")]
    [InlineData("not-json")]
    public async Task Project_create_rejects_invalid_names_and_slugs(string json)
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.PostAsync(ProjectsPath, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await ManagementApiData.ReadJsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData("""{"name":"Valid","platform":"unity"}""")]
    [InlineData("""{"platform":null}""")]
    [InlineData("""{"name":"Valid","platform":null}""")]
    [InlineData("""{"name":"Valid","platform":123}""")]
    [InlineData("""{"name":"Valid","platform":["unity"]}""")]
    public async Task Project_create_rejects_a_platform_member(string json)
    {
        using var api = new ManagementApiClient(_fixture);
        using var response = await api.PostAsync(ProjectsPath, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await ManagementApiData.ReadJsonAsync(response);
        Assert.Equal(
            "platform is no longer a project setting; events carry their own platform",
            body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("""{"name":"Renamed","platform":"unity"}""")]
    [InlineData("""{"platform":null}""")]
    [InlineData("""{"name":"Renamed","platform":false}""")]
    public async Task Project_update_rejects_a_platform_member(string json)
    {
        using var api = new ManagementApiClient(_fixture);
        var created = await CreateProjectAsync(api, "Platform Patch", UniqueSlug("platform-patch"));
        var projectId = created.GetProperty("id").GetInt64();

        using var response = await api.PatchAsync($"{ProjectsPath}/{projectId}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await ManagementApiData.ReadJsonAsync(response);
        Assert.Equal(
            "platform is no longer a project setting; events carry their own platform",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Project_detail_patch_and_delete_follow_the_management_contract()
    {
        using var api = new ManagementApiClient(_fixture);

        var created = await CreateProjectAsync(api, "Contract Project", UniqueSlug("contract"));
        var projectId = created.GetProperty("id").GetInt64();

        using (var patch = await api.PatchAsync(
                   $"{ProjectsPath}/{projectId}",
                   """{"name":"Renamed Project"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

            var updated = await ManagementApiData.ReadJsonAsync(patch);
            Assert.Equal("Renamed Project", updated.GetProperty("name").GetString());
            Assert.False(updated.TryGetProperty("platform", out _));
            Assert.Equal(created.GetProperty("slug").GetString(), updated.GetProperty("slug").GetString());
            Assert.Equal(created.GetProperty("dsn").GetString(), updated.GetProperty("dsn").GetString());
            Assert.Empty(Platforms(updated));
        }

        using (var invalidPatch = await api.PatchAsync($"{ProjectsPath}/{projectId}", """{"name":"  "}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidPatch.StatusCode);
        }

        using (var missing = await api.GetAsync($"{ProjectsPath}/{MissingId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var missingPatch = await api.PatchAsync(
                   $"{ProjectsPath}/{MissingId}",
                   """{"name":"Nope"}"""))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingPatch.StatusCode);
        }

        using (var missingDelete = await api.DeleteAsync($"{ProjectsPath}/{MissingId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
        }
    }

    [Fact]
    public async Task Project_delete_cascades_to_issues_events_labels_and_keys_but_keeps_messages()
    {
        using var api = new ManagementApiClient(_fixture);

        var created = await CreateProjectAsync(api, "Cascade Project", UniqueSlug("cascade"));
        var projectId = created.GetProperty("id").GetInt64();

        await ManagementApiData.IngestMessageEventAsync(
            api.Raw,
            projectId,
            DateTimeOffset.UtcNow,
            "Cascade issue",
            level: "error");

        var issues = await ManagementApiData.GetIssuesAsync(api, projectId);
        var issueId = ManagementApiData.IssueIdByTitle(issues, "Cascade issue");

        using (var labels = await api.PatchAsync(
                   $"/api/manage/issues/{issueId}",
                   """{"labels":["billing","crash"]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, labels.StatusCode);
        }

        var messageId = Guid.NewGuid();
        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
             insert into messages (id, project_id, received_at, endpoint, message)
             values ('{messageId}', {projectId}, now(), 'POST /api/1/messages', 'legacy row')
             """);

        Assert.Equal(1, await CountAsync("project_keys", projectId));
        Assert.Equal(1, await CountAsync("issues", projectId));
        Assert.Equal(1, await CountAsync("events", projectId));
        Assert.Equal(2, await CountAsync("labels", projectId));
        Assert.Equal(2, await CountIssueLabelsAsync(projectId));
        Assert.Equal(1, await CountAsync("messages", projectId));

        using (var delete = await api.DeleteAsync($"{ProjectsPath}/{projectId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        using (var secondDelete = await api.DeleteAsync($"{ProjectsPath}/{projectId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, secondDelete.StatusCode);
        }

        Assert.Equal(0, await CountAsync("project_keys", projectId));
        Assert.Equal(0, await CountAsync("issues", projectId));
        Assert.Equal(0, await CountAsync("events", projectId));
        Assert.Equal(0, await CountAsync("labels", projectId));
        Assert.Equal(0, await CountIssueLabelsAsync(projectId));
        Assert.Equal(1, await CountAsync("messages", projectId));

        using var detail = await api.GetAsync($"{ProjectsPath}/{projectId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        using var keys = await api.GetAsync($"{ProjectsPath}/{projectId}/keys");
        Assert.Equal(HttpStatusCode.NotFound, keys.StatusCode);
    }

    [Fact]
    public async Task Project_keys_can_be_listed_created_updated_and_revoked()
    {
        using var api = new ManagementApiClient(_fixture);

        var created = await CreateProjectAsync(api, "Key Project", UniqueSlug("keys"));
        var projectId = created.GetProperty("id").GetInt64();

        var initialKeys = await GetKeysAsync(api, projectId);
        var initialKey = Assert.Single(initialKeys);
        Assert.Null(initialKey.GetProperty("label").GetString());
        Assert.True(initialKey.GetProperty("is_active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, initialKey.GetProperty("rate_limit_per_minute").ValueKind);
        Assert.Equal(JsonValueKind.Null, initialKey.GetProperty("revoked_at").ValueKind);
        Assert.Equal(created.GetProperty("dsn").GetString(), initialKey.GetProperty("dsn").GetString());

        var second = await CreateKeyAsync(api, projectId, "ci", rateLimit: 10);
        var secondId = second.GetProperty("id").GetInt64();

        Assert.Equal(10, second.GetProperty("rate_limit_per_minute").GetInt32());
        Assert.Matches("^[0-9a-f]{32}$", second.GetProperty("public_key").GetString()!);
        Assert.EndsWith($"/{projectId}", second.GetProperty("dsn").GetString(), StringComparison.Ordinal);

        var listed = await GetKeysAsync(api, projectId);
        Assert.Equal(
            listed.Select(key => key.GetProperty("id").GetInt64()).OrderBy(id => id),
            listed.Select(key => key.GetProperty("id").GetInt64()));
        Assert.Equal(2, listed.Count);

        using (var patch = await api.PatchAsync(
                   $"/api/manage/keys/{secondId}",
                   """{"label":"nightly","rate_limit_per_minute":5}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

            var updated = await ManagementApiData.ReadJsonAsync(patch);
            Assert.Equal("nightly", updated.GetProperty("label").GetString());
            Assert.Equal(5, updated.GetProperty("rate_limit_per_minute").GetInt32());
            Assert.True(updated.GetProperty("is_active").GetBoolean());
        }

        // An omitted field keeps its value.
        using (var labelOnly = await api.PatchAsync(
                   $"/api/manage/keys/{secondId}",
                   """{"label":"nightly-2"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, labelOnly.StatusCode);

            var updated = await ManagementApiData.ReadJsonAsync(labelOnly);
            Assert.Equal("nightly-2", updated.GetProperty("label").GetString());
            Assert.Equal(5, updated.GetProperty("rate_limit_per_minute").GetInt32());
        }

        // An explicit null clears the rate limit.
        using (var clearLimit = await api.PatchAsync(
                   $"/api/manage/keys/{secondId}",
                   """{"rate_limit_per_minute":null}"""))
        {
            Assert.Equal(HttpStatusCode.OK, clearLimit.StatusCode);

            var updated = await ManagementApiData.ReadJsonAsync(clearLimit);
            Assert.Equal(JsonValueKind.Null, updated.GetProperty("rate_limit_per_minute").ValueKind);
            Assert.Equal("nightly-2", updated.GetProperty("label").GetString());
        }

        using (var revokeFlag = await api.PatchAsync(
                   $"/api/manage/keys/{secondId}",
                   """{"is_active":false}"""))
        {
            Assert.Equal(HttpStatusCode.OK, revokeFlag.StatusCode);

            var updated = await ManagementApiData.ReadJsonAsync(revokeFlag);
            Assert.False(updated.GetProperty("is_active").GetBoolean());
            Assert.Equal(JsonValueKind.String, updated.GetProperty("revoked_at").ValueKind);
        }

        using (var reactivate = await api.PatchAsync(
                   $"/api/manage/keys/{secondId}",
                   """{"is_active":true}"""))
        {
            Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);

            var updated = await ManagementApiData.ReadJsonAsync(reactivate);
            Assert.True(updated.GetProperty("is_active").GetBoolean());
            Assert.Equal(JsonValueKind.Null, updated.GetProperty("revoked_at").ValueKind);
        }

        using (var delete = await api.DeleteAsync($"/api/manage/keys/{secondId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        // Revoking retains the row and its revocation timestamp.
        var afterRevoke = (await GetKeysAsync(api, projectId))
            .Single(key => key.GetProperty("id").GetInt64() == secondId);
        Assert.False(afterRevoke.GetProperty("is_active").GetBoolean());
        Assert.Equal(JsonValueKind.String, afterRevoke.GetProperty("revoked_at").ValueKind);
        Assert.Equal(2, (await GetKeysAsync(api, projectId)).Count);

        using (var revokeInitial = await api.DeleteAsync(
                   $"/api/manage/keys/{initialKey.GetProperty("id").GetInt64()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revokeInitial.StatusCode);
        }

        // With no active key left, the detail exposes no DSN.
        using var detailResponse = await api.GetAsync($"{ProjectsPath}/{projectId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

        var detail = await ManagementApiData.ReadJsonAsync(detailResponse);
        Assert.Equal(0, detail.GetProperty("key_count").GetInt64());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("dsn").ValueKind);
    }

    [Fact]
    public async Task Key_endpoints_report_invalid_limits_and_unknown_ids()
    {
        using var api = new ManagementApiClient(_fixture);

        var created = await CreateProjectAsync(api, "Invalid Key Project", UniqueSlug("invalid-keys"));
        var projectId = created.GetProperty("id").GetInt64();
        var keyId = (await GetKeysAsync(api, projectId))[0].GetProperty("id").GetInt64();

        using (var negativeCreate = await api.PostAsync(
                   $"{ProjectsPath}/{projectId}/keys",
                   """{"rate_limit_per_minute":-1}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, negativeCreate.StatusCode);
        }

        using (var negativePatch = await api.PatchAsync(
                   $"/api/manage/keys/{keyId}",
                   """{"rate_limit_per_minute":-1}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, negativePatch.StatusCode);
        }

        using (var nonInteger = await api.PatchAsync(
                   $"/api/manage/keys/{keyId}",
                   """{"rate_limit_per_minute":1.5}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, nonInteger.StatusCode);
        }

        using (var wrongType = await api.PatchAsync(
                   $"/api/manage/keys/{keyId}",
                   """{"is_active":"yes"}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        }

        using (var missingKeyPatch = await api.PatchAsync(
                   $"/api/manage/keys/{MissingId}",
                   """{"label":"ghost"}"""))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingKeyPatch.StatusCode);
        }

        using (var missingKeyDelete = await api.DeleteAsync($"/api/manage/keys/{MissingId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingKeyDelete.StatusCode);
        }

        using (var missingProjectKeys = await api.GetAsync($"{ProjectsPath}/{MissingId}/keys"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingProjectKeys.StatusCode);
        }

        using (var missingProjectCreate = await api.PostAsync(
                   $"{ProjectsPath}/{MissingId}/keys",
                   "{}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingProjectCreate.StatusCode);
        }
    }

    [Fact]
    public async Task Project_management_endpoints_require_the_admin_bearer_token()
    {
        using var api = new ManagementApiClient(_fixture);

        var endpoints = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, ProjectsPath),
            (HttpMethod.Post, ProjectsPath),
            (HttpMethod.Get, $"{ProjectsPath}/1"),
            (HttpMethod.Patch, $"{ProjectsPath}/1"),
            (HttpMethod.Delete, $"{ProjectsPath}/1"),
            (HttpMethod.Get, $"{ProjectsPath}/1/keys"),
            (HttpMethod.Post, $"{ProjectsPath}/1/keys"),
            (HttpMethod.Patch, "/api/manage/keys/1"),
            (HttpMethod.Delete, "/api/manage/keys/1"),
        };

        foreach (var (method, path) in endpoints)
        {
            using var response = await api.SendAsync(
                method,
                path,
                json: method == HttpMethod.Get || method == HttpMethod.Delete ? null : "{}",
                token: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var wrongToken = await api.GetAsync(ProjectsPath, token: "wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
    }

    [Fact]
    public async Task An_https_public_relay_url_produces_an_https_dsn()
    {
        const string relayUrl = "https://relay.example.com:8443";

        using var factory = new ProcessorApplicationFactory(_fixture.ConnectionString, relayUrl);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ProjectsPath)
        {
            Content = new StringContent(
                $$"""{"name":"Secure Project","slug":"{{UniqueSlug("secure")}}"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ProcessorApplicationFactory.AdminToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await ManagementApiData.ReadJsonAsync(response);
        var id = created.GetProperty("id").GetInt64();
        var key = (await GetKeysAsync(factory, id))[0].GetProperty("public_key").GetString();

        Assert.Equal($"https://{key}@relay.example.com:8443/{id}", created.GetProperty("dsn").GetString());
        Assert.StartsWith("https://", created.GetProperty("dsn").GetString(), StringComparison.Ordinal);
    }

    private static async Task<JsonElement> CreateProjectAsync(
        ManagementApiClient api,
        string name,
        string? slug)
    {
        var body = new JsonObject { ["name"] = name };
        if (slug is not null)
        {
            body["slug"] = slug;
        }

        using var response = await api.PostAsync(ProjectsPath, body.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ManagementApiData.ReadJsonAsync(response);
    }

    private static List<string> Platforms(JsonElement project) =>
        project.GetProperty("platforms")
            .EnumerateArray()
            .Select(platform => platform.GetString()!)
            .ToList();

    private static async Task<JsonElement> CreateKeyAsync(
        ManagementApiClient api,
        long projectId,
        string? label,
        int? rateLimit)
    {
        var body = new JsonObject();
        if (label is not null)
        {
            body["label"] = label;
        }

        if (rateLimit is not null)
        {
            body["rate_limit_per_minute"] = rateLimit.Value;
        }

        using var response = await api.PostAsync($"{ProjectsPath}/{projectId}/keys", body.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ManagementApiData.ReadJsonAsync(response);
    }

    private static async Task<List<JsonElement>> GetKeysAsync(ManagementApiClient api, long projectId)
    {
        using var response = await api.GetAsync($"{ProjectsPath}/{projectId}/keys");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ManagementApiData.ReadJsonAsync(response))
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToList();
    }

    private static async Task<List<JsonElement>> GetKeysAsync(ProcessorApplicationFactory factory, long projectId)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ProjectsPath}/{projectId}/keys");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ProcessorApplicationFactory.AdminToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ManagementApiData.ReadJsonAsync(response))
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToList();
    }

    private Task<long> CountAsync(string table, long projectId) =>
        TestDatabase.QuerySingleLongAsync(
            _fixture.ConnectionString,
            $"select count(*) from {table} where project_id = {projectId}");

    private Task<long> CountIssueLabelsAsync(long projectId) =>
        TestDatabase.QuerySingleLongAsync(
            _fixture.ConnectionString,
            $"""
             select count(*)
             from issue_labels il
             join issues i on i.id = il.issue_id
             where i.project_id = {projectId}
             """);

    private static string UniqueSlug(string? prefix) =>
        prefix is null
            ? $"slug-{Guid.NewGuid():N}"
            : Regex.Replace($"{prefix}-{Guid.NewGuid():N}", "[^a-z0-9-]", string.Empty);
}
