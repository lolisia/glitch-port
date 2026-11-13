using System.Globalization;
using System.Text;
using System.Text.Json;
using GlitchPort.Collector.Contracts;
using Npgsql;

namespace GlitchPort.Processor.Tests;

internal static class TestData
{
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

    public static StringContent CreateEnvelopeContent(IEnumerable<Envelope> envelopes)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            envelopes.ToArray(),
            ContractsJsonContext.Default.EnvelopeArray);

        return new StringContent(Encoding.UTF8.GetString(json), Encoding.UTF8, "application/json");
    }
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

    public static async Task<long> CountMessagesAsync(string connectionString, Guid eventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from messages where id = @id", connection);
        command.Parameters.AddWithValue("id", eventId);

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
