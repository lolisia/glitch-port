using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// Sends management API requests with the admin token by default.
/// </summary>
internal sealed class ManagementApiClient : IDisposable
{
    private readonly HttpClient _client;

    public ManagementApiClient(ProcessorFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _client = fixture.CreateClient();
    }

    /// <summary>The underlying client, used to seed events through the ingest endpoints.</summary>
    public HttpClient Raw => _client;

    public Task<HttpResponseMessage> GetAsync(string path, string? token = ProcessorApplicationFactory.AdminToken) =>
        SendAsync(HttpMethod.Get, path, token: token);

    public Task<HttpResponseMessage> PostAsync(
        string path,
        string json,
        string? token = ProcessorApplicationFactory.AdminToken) =>
        SendAsync(HttpMethod.Post, path, json, token);

    public Task<HttpResponseMessage> PatchAsync(
        string path,
        string json,
        string? token = ProcessorApplicationFactory.AdminToken) =>
        SendAsync(HttpMethod.Patch, path, json, token);

    public Task<HttpResponseMessage> DeleteAsync(string path, string? token = ProcessorApplicationFactory.AdminToken) =>
        SendAsync(HttpMethod.Delete, path, token: token);

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json = null,
        string? token = ProcessorApplicationFactory.AdminToken)
    {
        using var request = new HttpRequestMessage(method, path);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await _client.SendAsync(request);
    }

    public void Dispose() => _client.Dispose();
}

internal static class ManagementApiData
{
    public const string ProjectIssuesPath = "/api/manage/projects";

    public static async Task<Guid> IngestEventAsync(
        HttpClient client,
        long projectId,
        DateTimeOffset timestamp,
        string fingerprint,
        string level = "error",
        string? environment = "production",
        string? release = "star-raiders@1.2.3")
    {
        var payload = TestData.CreateExceptionPayload(
            level: level,
            timestamp: timestamp,
            environment: environment,
            release: release,
            fingerprint: [fingerprint]);
        var envelope = TestData.CreatePayloadEnvelope(Guid.NewGuid(), projectId, timestamp, payload);

        await StoreEnvelopeAsync(client, envelope);
        return envelope.EventId;
    }

    public static async Task<Guid> IngestMessageEventAsync(
        HttpClient client,
        long projectId,
        DateTimeOffset timestamp,
        string message,
        string level = "info",
        string? environment = "production",
        string? release = "star-raiders@1.2.3")
    {
        var payload = new JsonObject
        {
            ["level"] = level,
            ["message"] = message,
            ["timestamp"] = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };

        if (environment is not null)
        {
            payload["environment"] = environment;
        }

        if (release is not null)
        {
            payload["release"] = release;
        }

        var envelope = TestData.CreatePayloadEnvelope(Guid.NewGuid(), projectId, timestamp, payload.ToJsonString());

        await StoreEnvelopeAsync(client, envelope);
        return envelope.EventId;
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> GetIssuesAsync(
        ManagementApiClient api,
        long projectId,
        string query = "?status=all")
    {
        using var response = await api.GetAsync($"{ProjectIssuesPath}/{projectId}/issues{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    public static async Task<Dictionary<string, long>> IssueIdsByReleaseAsync(
        ManagementApiClient api,
        long projectId)
    {
        var root = await GetIssuesAsync(api, projectId);
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("last_release").ValueKind == JsonValueKind.String)
            {
                ids[item.GetProperty("last_release").GetString()!] = item.GetProperty("id").GetInt64();
            }
        }

        return ids;
    }

    public static long IssueIdByTitle(JsonElement issueList, string title) =>
        issueList.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("title").GetString() == title)
            .GetProperty("id")
            .GetInt64();

    public static async Task<long> InsertIssueAsync(
        string connectionString,
        long projectId,
        string title,
        DateTimeOffset firstSeen,
        DateTimeOffset lastSeen,
        string status = "unresolved",
        long eventCount = 0)
    {
        var fingerprint = Guid.NewGuid().ToString("N");
        await TestDatabase.ExecuteAsync(
            connectionString,
            $"""
             insert into issues (project_id, fingerprint, title, level, platform, status, first_seen, last_seen, event_count)
             values ({projectId}, '{fingerprint}', '{title.Replace("'", "''")}', 'error', 'unity', '{status}',
                     '{firstSeen.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}',
                     '{lastSeen.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}',
                     {eventCount})
             """);

        return await TestDatabase.QuerySingleLongAsync(
            connectionString,
            $"select id from issues where project_id = {projectId} and fingerprint = '{fingerprint}'");
    }

    private static async Task StoreEnvelopeAsync(HttpClient client, GlitchPort.Collector.Contracts.Envelope envelope)
    {
        using var response = await TestData.PostEnvelopesAsync(
            client,
            ProcessorApplicationFactory.InternalSharedSecret,
            [envelope]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await TestData.ReadStoredAsync(response));
    }
}
