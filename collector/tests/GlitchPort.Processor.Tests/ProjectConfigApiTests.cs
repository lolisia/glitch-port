using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ProjectConfigApiTests
{
    private const string ConfigPath = "/internal/project-configs";
    private const string ProjectsPath = "/api/manage/projects";

    private readonly ProcessorFixture _fixture;

    public ProjectConfigApiTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task The_internal_endpoint_requires_the_shared_secret()
    {
        using var client = _fixture.CreateClient();

        using (var missing = await GetConfigAsync(client, secret: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        }

        using (var wrong = await GetConfigAsync(client, secret: "wrong-secret"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using (var valid = await GetConfigAsync(client, ProcessorApplicationFactory.InternalSharedSecret))
        {
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        }
    }

    [Fact]
    public async Task The_config_contains_only_active_keys_with_their_rate_limits_and_an_etag()
    {
        using var api = new ManagementApiClient(_fixture);
        using var client = _fixture.CreateClient();

        var created = await CreateProjectAsync(api, "Config Project", UniqueSlug("config"));
        var projectId = created.GetProperty("id").GetInt64();

        var initialKey = (await GetKeysAsync(api, projectId))[0];
        var initialKeyId = initialKey.GetProperty("id").GetInt64();
        var initialPublicKey = initialKey.GetProperty("public_key").GetString();

        var limitedKey = await CreateKeyAsync(api, projectId, "limited", 7);
        var limitedPublicKey = limitedKey.GetProperty("public_key").GetString();

        using var firstResponse = await GetConfigAsync(client, ProcessorApplicationFactory.InternalSharedSecret);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var first = await ManagementApiData.ReadJsonAsync(firstResponse);
        var version = first.GetProperty("version").GetString();
        Assert.Matches("^[0-9a-f]{64}$", version);

        var etag = firstResponse.Headers.GetValues("ETag").Single();
        Assert.Equal($"\"{version}\"", etag);

        var project = FindProject(first, projectId);
        var keys = project.GetProperty("keys").EnumerateArray().Select(key => key.Clone()).ToList();

        Assert.Equal(2, keys.Count);
        Assert.Equal(initialPublicKey, keys[0].GetProperty("public_key").GetString());
        Assert.Equal(JsonValueKind.Null, keys[0].GetProperty("rate_limit_per_minute").ValueKind);
        Assert.Equal(limitedPublicKey, keys[1].GetProperty("public_key").GetString());
        Assert.Equal(7, keys[1].GetProperty("rate_limit_per_minute").GetInt32());

        // The response order is project id, then key id.
        var projectIds = first.GetProperty("projects").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt64())
            .ToList();
        Assert.Equal(projectIds.OrderBy(id => id), projectIds);

        using (var revoke = await api.DeleteAsync($"/api/manage/keys/{initialKeyId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using var secondResponse = await GetConfigAsync(client, ProcessorApplicationFactory.InternalSharedSecret);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        var second = await ManagementApiData.ReadJsonAsync(secondResponse);
        var secondKeys = FindProject(second, projectId).GetProperty("keys").EnumerateArray()
            .Select(key => key.Clone())
            .ToList();

        var remaining = Assert.Single(secondKeys);
        Assert.Equal(limitedPublicKey, remaining.GetProperty("public_key").GetString());

        // The version changed with the revocation and no inactive key is exposed.
        Assert.NotEqual(version, second.GetProperty("version").GetString());
    }

    [Fact]
    public async Task A_matching_if_none_match_returns_304_with_the_same_etag()
    {
        using var api = new ManagementApiClient(_fixture);
        using var client = _fixture.CreateClient();

        var created = await CreateProjectAsync(api, "Etag Project", UniqueSlug("etag"));
        Assert.True(created.GetProperty("id").GetInt64() > 0);

        using var firstResponse = await GetConfigAsync(client, ProcessorApplicationFactory.InternalSharedSecret);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var etag = firstResponse.Headers.GetValues("ETag").Single();
        var version = (await ManagementApiData.ReadJsonAsync(firstResponse)).GetProperty("version").GetString();
        Assert.Equal($"\"{version}\"", etag);

        using (var notModified = await GetConfigAsync(
                   client,
                   ProcessorApplicationFactory.InternalSharedSecret,
                   ifNoneMatch: etag))
        {
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            Assert.Equal(etag, notModified.Headers.GetValues("ETag").Single());
            Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
        }

        using (var weakMatch = await GetConfigAsync(
                   client,
                   ProcessorApplicationFactory.InternalSharedSecret,
                   ifNoneMatch: $"W/{etag}"))
        {
            Assert.Equal(HttpStatusCode.NotModified, weakMatch.StatusCode);
        }

        using (var stale = await GetConfigAsync(
                   client,
                   ProcessorApplicationFactory.InternalSharedSecret,
                   ifNoneMatch: "\"stale-version\""))
        {
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        }

        // Nothing changed, so the version is stable.
        using var repeat = await GetConfigAsync(client, ProcessorApplicationFactory.InternalSharedSecret);
        var repeatVersion = (await ManagementApiData.ReadJsonAsync(repeat)).GetProperty("version").GetString();
        Assert.Equal(version, repeatVersion);
    }

    [Fact]
    public async Task The_version_changes_when_keys_and_rate_limits_change()
    {
        using var api = new ManagementApiClient(_fixture);
        using var client = _fixture.CreateClient();

        var beforeCreate = await GetVersionAsync(client);

        var created = await CreateProjectAsync(api, "Version Project", UniqueSlug("version"));
        var projectId = created.GetProperty("id").GetInt64();

        var initialVersion = await GetVersionAsync(client);
        Assert.NotEqual(beforeCreate, initialVersion);

        var key = await CreateKeyAsync(api, projectId, "versioned", rateLimit: null);
        var keyId = key.GetProperty("id").GetInt64();

        var afterKeyCreate = await GetVersionAsync(client);
        Assert.NotEqual(initialVersion, afterKeyCreate);

        using (var patch = await api.PatchAsync(
                   $"/api/manage/keys/{keyId}",
                   """{"rate_limit_per_minute":12}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        var afterRateLimit = await GetVersionAsync(client);
        Assert.NotEqual(afterKeyCreate, afterRateLimit);

        using (var revoke = await api.DeleteAsync($"/api/manage/keys/{keyId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        var afterRevoke = await GetVersionAsync(client);
        Assert.NotEqual(afterRateLimit, afterRevoke);

        // Revoking an already revoked key is not exposed, so the config is
        // unchanged and the version stays the same.
        using (var secondRevoke = await api.DeleteAsync($"/api/manage/keys/{keyId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, secondRevoke.StatusCode);
        }

        Assert.Equal(afterRevoke, await GetVersionAsync(client));

        using (var delete = await api.DeleteAsync($"{ProjectsPath}/{projectId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        Assert.NotEqual(afterRevoke, await GetVersionAsync(client));
    }

    private static async Task<HttpResponseMessage> GetConfigAsync(
        HttpClient client,
        string? secret,
        string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ConfigPath);

        if (secret is not null)
        {
            request.Headers.TryAddWithoutValidation(TestData.InternalHeader, secret);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await client.SendAsync(request);
    }

    private async Task<string> GetVersionAsync(HttpClient client)
    {
        using var response = await GetConfigAsync(client, ProcessorApplicationFactory.InternalSharedSecret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await ManagementApiData.ReadJsonAsync(response)).GetProperty("version").GetString()!;
    }

    private static JsonElement FindProject(JsonElement config, long projectId) =>
        config.GetProperty("projects")
            .EnumerateArray()
            .Single(project => project.GetProperty("id").GetInt64() == projectId)
            .Clone();

    private static async Task<JsonElement> CreateProjectAsync(
        ManagementApiClient api,
        string name,
        string slug)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["slug"] = slug,
        };

        using var response = await api.PostAsync(ProjectsPath, body.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ManagementApiData.ReadJsonAsync(response);
    }

    private static async Task<JsonElement> CreateKeyAsync(
        ManagementApiClient api,
        long projectId,
        string label,
        int? rateLimit)
    {
        var body = new JsonObject { ["label"] = label };
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

    private static string UniqueSlug(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
