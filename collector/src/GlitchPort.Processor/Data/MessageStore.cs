using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Data;

/// <summary>
/// Reads the management message list. Structured events and the legacy
/// <c>messages</c> table are combined so existing consumers keep working.
/// </summary>
public sealed class MessageStore
{
    private const string ListMessagesSql = """
        select id as "Id",
               received_at as "ReceivedAt",
               endpoint as "Endpoint",
               message as "Message"
        from (
            select id, received_at, endpoint, message
            from events
            where message is not null
            union all
            select id, received_at, endpoint, message
            from messages
        ) stored_messages
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
