using System.Data;
using Dapper;
using GlitchPort.Collector.Contracts;
using Npgsql;

namespace GlitchPort.Processor.Data;

/// <summary>
/// Stores envelopes in PostgreSQL and reads them back for the management API.
/// </summary>
public sealed class MessageStore
{
    private const string InsertEnvelopeSql = """
        insert into messages (id, project_id, received_at, endpoint, message, client_timestamp, sdk_name, sdk_version)
        values (@Id, @ProjectId, @ReceivedAt, @Endpoint, @Message, @ClientTimestamp, @SdkName, @SdkVersion)
        on conflict (id) do nothing
        """;

    private const string ListMessagesSql = """
        select id as "Id",
               received_at as "ReceivedAt",
               endpoint as "Endpoint",
               message as "Message"
        from messages
        order by received_at desc
        limit @Limit
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public MessageStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Inserts every envelope in one transaction, ignoring envelopes whose id
    /// already exists.
    /// </summary>
    /// <returns>The number of rows actually inserted.</returns>
    public async Task<int> InsertAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        if (envelopes.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var stored = 0;

        foreach (var envelope in envelopes)
        {
            var parameters = new DynamicParameters();
            parameters.Add("Id", envelope.EventId, DbType.Guid);
            parameters.Add("ProjectId", envelope.ProjectId, DbType.Int64);
            parameters.Add("ReceivedAt", envelope.ReceivedAt.ToUniversalTime(), DbType.DateTimeOffset);
            parameters.Add("Endpoint", envelope.Endpoint, DbType.String);
            parameters.Add("Message", envelope.Message, DbType.String);
            parameters.Add("ClientTimestamp", envelope.ClientTimestamp?.ToUniversalTime(), DbType.DateTimeOffset);
            parameters.Add("SdkName", envelope.SdkName, DbType.String);
            parameters.Add("SdkVersion", envelope.SdkVersion, DbType.String);

            stored += await connection
                .ExecuteAsync(new CommandDefinition(
                    InsertEnvelopeSql,
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    /// <summary>
    /// Returns the newest messages first.
    /// </summary>
    public async Task<List<MessageListItem>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection
            .QueryAsync<MessageRow>(new CommandDefinition(
                ListMessagesSql,
                new { Limit = limit },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows
            .Select(row => new MessageListItem(
                row.Id,
                new DateTimeOffset(DateTime.SpecifyKind(row.ReceivedAt, DateTimeKind.Utc)),
                row.Endpoint,
                row.Message))
            .ToList();
    }

    private sealed class MessageRow
    {
        public Guid Id { get; init; }

        public DateTime ReceivedAt { get; init; }

        public string Endpoint { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;
    }
}
