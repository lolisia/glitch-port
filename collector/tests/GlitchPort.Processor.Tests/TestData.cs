using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlitchPort.Collector.Contracts;
using Npgsql;

namespace GlitchPort.Processor.Tests;

internal static class TestData
{
    public const string InternalHeader = "X-GlitchPort-Internal";

    private static readonly (string Module, string Function, bool InApp)[] DefaultFrames =
    [
        ("UnityEngine.CoreModule", "UnityAction.Invoke", false),
        ("Assembly-CSharp", "PlayerController.Update", true),
        ("Assembly-CSharp", "Weapon.Fire", true),
    ];

    private static long _projectIdCounter = 100_000;

    public static long NextProjectId() => Interlocked.Increment(ref _projectIdCounter);

    public static Envelope CreateEnvelope(Guid eventId, DateTimeOffset receivedAt, string message) => new()
    {
        EventId = eventId,
        ProjectId = 7,
        ReceivedAt = receivedAt,
        Endpoint = "POST /api/1/messages",
        Message = message,
        ClientTimestamp = receivedAt.AddSeconds(-1),
        SdkName = "glitch-port-tests",
        SdkVersion = "1.0.0",
    };

    public static Envelope CreatePayloadEnvelope(
        Guid eventId,
        long projectId,
        DateTimeOffset receivedAt,
        string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);

        return new Envelope
        {
            Version = 2,
            EventId = eventId,
            ProjectId = projectId,
            ReceivedAt = receivedAt,
            Endpoint = "POST /api/1/events",
            Payload = document.RootElement.Clone(),
        };
    }

    /// <summary>
    /// Builds a protocol v1 event payload whose exception has the same
    /// fingerprint for every call, unless <paramref name="fingerprint"/> is
    /// supplied explicitly.
    /// </summary>
    public static string CreateExceptionPayload(
        string level = "error",
        string platform = "unity",
        string? message = "NullReferenceException: Object reference not set to an instance of an object",
        string? release = "star-raiders@1.2.3",
        string? environment = "production",
        string? userId = "player-123",
        DateTimeOffset? timestamp = null,
        IReadOnlyList<string>? fingerprint = null,
        IReadOnlyList<(string Module, string Function, bool InApp)>? frames = null)
    {
        var payload = new JsonObject
        {
            ["level"] = level,
            ["platform"] = platform,
        };

        if (message is not null)
        {
            payload["message"] = message;
        }

        if (timestamp is { } moment)
        {
            payload["timestamp"] = moment
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
        }

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

        var frameArray = new JsonArray();
        foreach (var frame in frames ?? DefaultFrames)
        {
            frameArray.Add(new JsonObject
            {
                ["function"] = frame.Function,
                ["module"] = frame.Module,
                ["filename"] = "Assets/Scripts/Weapon.cs",
                ["in_app"] = frame.InApp,
            });
        }

        payload["exception"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "System.NullReferenceException",
                ["value"] = "Object reference not set to an instance of an object",
                ["module"] = "Assembly-CSharp",
                ["stacktrace"] = new JsonObject { ["frames"] = frameArray },
            },
        };

        if (fingerprint is { Count: > 0 })
        {
            payload["fingerprint"] = new JsonArray(
                fingerprint.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
        }

        return payload.ToJsonString();
    }

    public static StringContent CreateEnvelopeContent(IEnumerable<Envelope> envelopes)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            envelopes.ToArray(),
            ContractsJsonContext.Default.EnvelopeArray);

        return new StringContent(Encoding.UTF8.GetString(json), Encoding.UTF8, "application/json");
    }

    public static async Task<HttpResponseMessage> PostEnvelopesAsync(
        HttpClient client,
        string? secret,
        IEnumerable<Envelope> envelopes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/envelopes")
        {
            Content = CreateEnvelopeContent(envelopes),
        };

        if (secret is not null)
        {
            request.Headers.TryAddWithoutValidation(InternalHeader, secret);
        }

        return await client.SendAsync(request);
    }

    public static async Task<int> ReadStoredAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("stored").GetInt32();
    }

    public static HttpRequestMessage CreateAuthorizedGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ProcessorApplicationFactory.AdminToken);
        return request;
    }
}

internal sealed class IssueRow
{
    public long Id { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Culprit { get; init; }

    public string Level { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset LastSeen { get; init; }

    public long EventCount { get; init; }

    public string? LastRelease { get; init; }

    public string? LastEnvironment { get; init; }
}

internal sealed class EventRow
{
    public Guid Id { get; init; }

    public long ProjectId { get; init; }

    public long IssueId { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public string Level { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string? Environment { get; init; }

    public string? Release { get; init; }

    public string? UserId { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    public string? Message { get; init; }

    public string PayloadJson { get; init; } = "{}";
}

internal static class TestDatabase
{
    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> CountEventsAsync(string connectionString, Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from events where id = @id", connection);
        command.Parameters.AddWithValue("id", eventId);

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<long> CountEventsAsync(string connectionString, long projectId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from events where project_id = @project_id",
            connection);
        command.Parameters.AddWithValue("project_id", projectId);

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<long> CountIssuesAsync(string connectionString, long projectId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from issues where project_id = @project_id",
            connection);
        command.Parameters.AddWithValue("project_id", projectId);

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<IssueRow> GetIssueAsync(string connectionString, long projectId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select id,
                   fingerprint,
                   title,
                   culprit,
                   level,
                   platform,
                   status,
                   first_seen,
                   last_seen,
                   event_count,
                   last_release,
                   last_environment
            from issues
            where project_id = @project_id
            """,
            connection);
        command.Parameters.AddWithValue("project_id", projectId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No issue exists for project {projectId}.");
        }

        return new IssueRow
        {
            Id = reader.GetInt64(0),
            Fingerprint = reader.GetString(1),
            Title = reader.GetString(2),
            Culprit = reader.IsDBNull(3) ? null : reader.GetString(3),
            Level = reader.GetString(4),
            Platform = reader.GetString(5),
            Status = reader.GetString(6),
            FirstSeen = reader.GetFieldValue<DateTimeOffset>(7),
            LastSeen = reader.GetFieldValue<DateTimeOffset>(8),
            EventCount = reader.GetInt64(9),
            LastRelease = reader.IsDBNull(10) ? null : reader.GetString(10),
            LastEnvironment = reader.IsDBNull(11) ? null : reader.GetString(11),
        };
    }

    public static async Task<long> CountIssuesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from issues", connection);

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<EventRow> GetEventAsync(string connectionString, Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select id,
                   project_id,
                   issue_id,
                   timestamp,
                   received_at,
                   level,
                   platform,
                   environment,
                   release,
                   user_id,
                   endpoint,
                   message,
                   payload::text
            from events
            where id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", eventId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"Event {eventId} was not stored.");
        }

        return new EventRow
        {
            Id = reader.GetGuid(0),
            ProjectId = reader.GetInt64(1),
            IssueId = reader.GetInt64(2),
            Timestamp = reader.GetFieldValue<DateTimeOffset>(3),
            ReceivedAt = reader.GetFieldValue<DateTimeOffset>(4),
            Level = reader.GetString(5),
            Platform = reader.GetString(6),
            Environment = reader.IsDBNull(7) ? null : reader.GetString(7),
            Release = reader.IsDBNull(8) ? null : reader.GetString(8),
            UserId = reader.IsDBNull(9) ? null : reader.GetString(9),
            Endpoint = reader.GetString(10),
            Message = reader.IsDBNull(11) ? null : reader.GetString(11),
            PayloadJson = reader.GetString(12),
        };
    }

    public static async Task<long> QuerySingleLongAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<List<string>> QueryStringsAsync(string connectionString, string sql)
    {
        var values = new List<string>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
