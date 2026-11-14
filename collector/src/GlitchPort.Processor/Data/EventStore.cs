using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using GlitchPort.Collector.Contracts;
using GlitchPort.Processor.Grouping;
using Npgsql;

namespace GlitchPort.Processor.Data;

/// <summary>
/// Stores structured events in PostgreSQL, grouping them into issues by
/// fingerprint.
/// </summary>
/// <remarks>
/// A batch is written in one transaction. Issue rows are acquired in
/// <c>(project_id, fingerprint)</c> order so that concurrent Processor
/// instances lock rows in the same order, and the whole batch is retried when
/// PostgreSQL reports a deadlock or a serialization failure.
/// </remarks>
public sealed class EventStore
{
    // One initial attempt plus up to three retries after a deadlock (40P01)
    // or serialization failure (40001).
    private const int MaxAttempts = 4;
    private const int MaxEventsPerInsert = 500;
    private const string OtherPlatform = "other";
    private const string DefaultLevel = "info";
    private const string ErrorLevel = "error";

    private static readonly string[] LevelNames = ["debug", "info", "warning", "error", "fatal"];

    private const string AcquireIssueSql = """
        insert into issues (
            project_id,
            fingerprint,
            title,
            culprit,
            level,
            platform,
            first_seen,
            last_seen,
            event_count,
            last_release,
            last_environment)
        values (
            @ProjectId,
            @Fingerprint,
            @Title,
            @Culprit,
            @Level,
            @Platform,
            @FirstSeen,
            @LastSeen,
            0,
            @LastRelease,
            @LastEnvironment)
        on conflict (project_id, fingerprint) do update set fingerprint = excluded.fingerprint
        returning id
        """;

    private const string UpdateIssueSql = """
        update issues
        set event_count = event_count + @EventCount,
            first_seen = least(first_seen, @FirstSeen),
            last_seen = greatest(last_seen, @LastSeen),
            last_release = @LastRelease,
            last_environment = @LastEnvironment,
            level = case
                when @LevelSeverity > case level
                    when 'fatal' then 4
                    when 'error' then 3
                    when 'warning' then 2
                    when 'info' then 1
                    else 0
                end
                then @Level
                else level
            end,
            status = case when status = 'resolved' then 'unresolved' else status end
        where id = @IssueId
        """;

    private const string InsertEventsPrefix = """
        insert into events (
            id,
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
            payload)
        values
        """;

    private const string InsertEventsSuffix = " on conflict (id) do nothing returning id, issue_id";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public EventStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Stores every envelope of the batch in one transaction, ignoring event
    /// ids that were already stored, and folds the new events into their
    /// issues.
    /// </summary>
    /// <param name="envelopes">Envelopes received from a Relay instance.</param>
    /// <param name="cancellationToken">Cancellation token for the whole batch.</param>
    /// <returns>The number of events that were actually inserted.</returns>
    public async Task<int> StoreAsync(
        IReadOnlyList<Envelope> envelopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        if (envelopes.Count == 0)
        {
            return 0;
        }

        var pending = Prepare(envelopes);
        if (pending.Count == 0)
        {
            return 0;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = await _dataSource
                    .OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);

                return await StoreBatchAsync(connection, pending, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (attempt < MaxAttempts && IsRetryable(exception))
            {
                // The transaction has been rolled back by StoreBatchAsync.
                // Retrying the complete batch keeps issue counts and
                // deduplication consistent under concurrent writers.
            }
        }
    }

    /// <summary>
    /// Converts the envelopes into events, removing duplicate ids inside the
    /// batch and sorting by <c>(project_id, fingerprint)</c> so concurrent
    /// instances acquire issue rows in the same order.
    /// </summary>
    private static List<PendingEvent> Prepare(IReadOnlyList<Envelope> envelopes)
    {
        var seen = new HashSet<Guid>();
        var pending = new List<PendingEvent>(envelopes.Count);

        foreach (var envelope in envelopes)
        {
            if (!seen.Add(envelope.EventId))
            {
                continue;
            }

            pending.Add(CreateEvent(envelope));
        }

        pending.Sort(static (left, right) =>
        {
            var comparison = left.ProjectId.CompareTo(right.ProjectId);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(left.Fingerprint, right.Fingerprint);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Timestamp.CompareTo(right.Timestamp);
            return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
        });

        return pending;
    }

    private static PendingEvent CreateEvent(Envelope envelope)
    {
        if (envelope.Version == 2
            && envelope.Payload is { ValueKind: JsonValueKind.Object } payload)
        {
            return CreateEventFromPayload(envelope, payload);
        }

        return CreateEventFromLegacyEnvelope(envelope);
    }

    private static PendingEvent CreateEventFromPayload(Envelope envelope, JsonElement payload)
    {
        var message = ReadString(payload, "message");
        var exceptions = ReadExceptions(payload);
        var customFingerprint = ReadStringArray(payload, "fingerprint");
        var logger = ReadString(payload, "logger");

        var grouping = Fingerprinter.Compute(new FingerprintInput(
            exceptions,
            message,
            logger,
            customFingerprint));

        var timestamp = (ReadTimestamp(payload) ?? envelope.ReceivedAt).ToUniversalTime();
        var level = NormalizeLevel(
            ReadString(payload, "level"),
            exceptions.Count > 0 ? ErrorLevel : DefaultLevel);

        return new PendingEvent(
            envelope.EventId,
            envelope.ProjectId,
            grouping.Fingerprint,
            grouping.Title,
            grouping.Culprit,
            level,
            NormalizePlatform(ReadString(payload, "platform")),
            ReadString(payload, "environment"),
            ReadString(payload, "release"),
            ReadUserId(payload),
            envelope.Endpoint,
            envelope.ReceivedAt.ToUniversalTime(),
            timestamp,
            message,
            payload.GetRawText());
    }

    private static PendingEvent CreateEventFromLegacyEnvelope(Envelope envelope)
    {
        var timestamp = (envelope.ClientTimestamp ?? envelope.ReceivedAt).ToUniversalTime();
        var grouping = Fingerprinter.Compute(new FingerprintInput(
            [],
            envelope.Message,
            Logger: null,
            CustomFingerprint: null));

        return new PendingEvent(
            envelope.EventId,
            envelope.ProjectId,
            grouping.Fingerprint,
            grouping.Title,
            grouping.Culprit,
            DefaultLevel,
            OtherPlatform,
            Environment: null,
            Release: null,
            UserId: null,
            envelope.Endpoint,
            envelope.ReceivedAt.ToUniversalTime(),
            timestamp,
            envelope.Message,
            BuildLegacyPayload(envelope, timestamp));
    }

    /// <summary>
    /// Builds the v1 event JSON kept as the payload of converted legacy
    /// envelopes.
    /// </summary>
    private static string BuildLegacyPayload(Envelope envelope, DateTimeOffset timestamp)
    {
        var payload = new JsonObject
        {
            ["message"] = envelope.Message,
            ["timestamp"] = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["level"] = DefaultLevel,
        };

        if (!string.IsNullOrEmpty(envelope.SdkName) || !string.IsNullOrEmpty(envelope.SdkVersion))
        {
            var sdk = new JsonObject();
            if (envelope.SdkName is not null)
            {
                sdk["name"] = envelope.SdkName;
            }

            if (envelope.SdkVersion is not null)
            {
                sdk["version"] = envelope.SdkVersion;
            }

            payload["sdk"] = sdk;
        }

        return payload.ToJsonString();
    }

    private static async Task<int> StoreBatchAsync(
        NpgsqlConnection connection,
        IReadOnlyList<PendingEvent> events,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var queued = await AcquireIssuesAsync(connection, transaction, events, cancellationToken)
                .ConfigureAwait(false);

            var inserted = await InsertEventsAsync(connection, transaction, queued, cancellationToken)
                .ConfigureAwait(false);

            await UpdateIssuesAsync(connection, transaction, inserted, cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return inserted.Count;
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<List<QueuedEvent>> AcquireIssuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<PendingEvent> events,
        CancellationToken cancellationToken)
    {
        var queued = new List<QueuedEvent>(events.Count);

        foreach (var group in events.GroupBy(
                     static item => (item.ProjectId, item.Fingerprint)))
        {
            // Events are sorted by timestamp inside a group, so the first one
            // is the event that seeds a newly created issue.
            var first = group.First();
            var issueId = await AcquireIssueAsync(connection, transaction, first, cancellationToken)
                .ConfigureAwait(false);

            foreach (var item in group)
            {
                queued.Add(new QueuedEvent(item, issueId));
            }
        }

        return queued;
    }

    private static async Task<long> AcquireIssueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PendingEvent first,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ProjectId", first.ProjectId, DbType.Int64);
        parameters.Add("Fingerprint", first.Fingerprint, DbType.String);
        parameters.Add("Title", first.Title, DbType.String);
        parameters.Add("Culprit", first.Culprit, DbType.String);
        parameters.Add("Level", first.Level, DbType.String);
        parameters.Add("Platform", first.Platform, DbType.String);
        parameters.Add("FirstSeen", first.Timestamp, DbType.DateTimeOffset);
        parameters.Add("LastSeen", first.Timestamp, DbType.DateTimeOffset);
        parameters.Add("LastRelease", first.Release, DbType.String);
        parameters.Add("LastEnvironment", first.Environment, DbType.String);

        return await connection
            .QuerySingleAsync<long>(new CommandDefinition(
                AcquireIssueSql,
                parameters,
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the events in chunks with a multi-row <c>insert ... on conflict
    /// do nothing returning</c> and returns only the rows PostgreSQL actually
    /// inserted.
    /// </summary>
    private static async Task<List<QueuedEvent>> InsertEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<QueuedEvent> events,
        CancellationToken cancellationToken)
    {
        var inserted = new List<QueuedEvent>(events.Count);
        var byId = events.ToDictionary(static item => item.Event.Id);

        for (var offset = 0; offset < events.Count; offset += MaxEventsPerInsert)
        {
            var chunk = events
                .Skip(offset)
                .Take(MaxEventsPerInsert)
                .ToList();

            var sql = BuildInsertEventsSql(chunk.Count);
            var parameters = new DynamicParameters();

            for (var index = 0; index < chunk.Count; index++)
            {
                var item = chunk[index];
                parameters.Add($"id{index}", item.Event.Id, DbType.Guid);
                parameters.Add($"projectId{index}", item.Event.ProjectId, DbType.Int64);
                parameters.Add($"issueId{index}", item.IssueId, DbType.Int64);
                parameters.Add($"timestamp{index}", item.Event.Timestamp, DbType.DateTimeOffset);
                parameters.Add($"receivedAt{index}", item.Event.ReceivedAt, DbType.DateTimeOffset);
                parameters.Add($"level{index}", item.Event.Level, DbType.String);
                parameters.Add($"platform{index}", item.Event.Platform, DbType.String);
                parameters.Add($"environment{index}", item.Event.Environment, DbType.String);
                parameters.Add($"release{index}", item.Event.Release, DbType.String);
                parameters.Add($"userId{index}", item.Event.UserId, DbType.String);
                parameters.Add($"endpoint{index}", item.Event.Endpoint, DbType.String);
                parameters.Add($"message{index}", item.Event.Message, DbType.String);
                parameters.Add($"payload{index}", item.Event.PayloadJson, DbType.String);
            }

            var rows = await connection
                .QueryAsync<InsertedEventRow>(new CommandDefinition(
                    sql,
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                if (byId.TryGetValue(row.Id, out var item))
                {
                    inserted.Add(item);
                }
            }
        }

        return inserted;
    }

    private static string BuildInsertEventsSql(int count)
    {
        var sql = new StringBuilder(InsertEventsPrefix);

        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                sql.Append(",\n");
            }

            sql.Append(CultureInfo.InvariantCulture, $"(@id{index}, @projectId{index}, @issueId{index}, @timestamp{index}, @receivedAt{index}, @level{index}, @platform{index}, @environment{index}, @release{index}, @userId{index}, @endpoint{index}, @message{index}, cast(@payload{index} as jsonb))");
        }

        sql.Append(InsertEventsSuffix);
        return sql.ToString();
    }

    /// <summary>
    /// Updates only the issues that gained rows: count and seen timestamps,
    /// the release and environment of the most recent event, a severity that
    /// never decreases, and <c>resolved</c> to <c>unresolved</c> reopening.
    /// </summary>
    private static async Task UpdateIssuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<QueuedEvent> inserted,
        CancellationToken cancellationToken)
    {
        foreach (var group in inserted
                     .GroupBy(static item => item.IssueId)
                     .OrderBy(static group => group.Key))
        {
            var mostRecent = group
                .OrderBy(static item => item.Event.Timestamp)
                .ThenBy(static item => item.Event.ReceivedAt)
                .ThenBy(static item => item.Event.Id)
                .Last();

            var severity = group.Max(static item => Severity(item.Event.Level));

            var parameters = new DynamicParameters();
            parameters.Add("IssueId", group.Key, DbType.Int64);
            parameters.Add("EventCount", group.Count(), DbType.Int64);
            parameters.Add("FirstSeen", group.Min(static item => item.Event.Timestamp), DbType.DateTimeOffset);
            parameters.Add("LastSeen", group.Max(static item => item.Event.Timestamp), DbType.DateTimeOffset);
            parameters.Add("LastRelease", mostRecent.Event.Release, DbType.String);
            parameters.Add("LastEnvironment", mostRecent.Event.Environment, DbType.String);
            parameters.Add("Level", LevelNames[severity], DbType.String);
            parameters.Add("LevelSeverity", severity, DbType.Int32);

            await connection
                .ExecuteAsync(new CommandDefinition(
                    UpdateIssueSql,
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure;

    private static async Task RollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // The connection is already unusable; the next attempt opens a
            // new one, and PostgreSQL rolls back on disconnect regardless.
        }
    }

    private static int Severity(string level) => level switch
    {
        "debug" => 0,
        "info" => 1,
        "warning" => 2,
        "error" => 3,
        "fatal" => 4,
        _ => 1,
    };

    private static string NormalizeLevel(string? level, string fallback)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return fallback;
        }

        var normalized = level.Trim().ToLowerInvariant();
        return Array.IndexOf(LevelNames, normalized) >= 0 ? normalized : fallback;
    }

    private static string NormalizePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? OtherPlatform : platform.Trim();

    /// <summary>
    /// Reads exceptions in wire order, from the innermost cause to the
    /// outermost exception, which is the order <see cref="Fingerprinter"/>
    /// expects.
    /// </summary>
    private static List<EventException> ReadExceptions(JsonElement payload)
    {
        var exceptions = new List<EventException>();

        if (!payload.TryGetProperty("exception", out var exceptionArray)
            || exceptionArray.ValueKind != JsonValueKind.Array)
        {
            return exceptions;
        }

        foreach (var exception in exceptionArray.EnumerateArray())
        {
            if (exception.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = ReadString(exception, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            exceptions.Add(new EventException(
                type.Trim(),
                ReadString(exception, "value"),
                ReadString(exception, "module"),
                ReadFrames(exception)));
        }

        return exceptions;
    }

    /// <summary>
    /// Reads frames in wire order, from the outermost call to the crash site,
    /// which is the order <see cref="Fingerprinter"/> expects.
    /// </summary>
    private static List<EventFrame> ReadFrames(JsonElement exception)
    {
        var frames = new List<EventFrame>();

        if (!exception.TryGetProperty("stacktrace", out var stackTrace)
            || stackTrace.ValueKind != JsonValueKind.Object
            || !stackTrace.TryGetProperty("frames", out var frameArray)
            || frameArray.ValueKind != JsonValueKind.Array)
        {
            return frames;
        }

        foreach (var frame in frameArray.EnumerateArray())
        {
            if (frame.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            frames.Add(new EventFrame(
                ReadString(frame, "function"),
                ReadString(frame, "module"),
                ReadString(frame, "filename"),
                ReadInteger(frame, "lineno"),
                ReadInteger(frame, "colno"),
                ReadBoolean(frame, "in_app")));
        }

        return frames;
    }

    private static string? ReadUserId(JsonElement payload) =>
        payload.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            ? ReadString(user, "id")
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement payload)
    {
        var value = ReadString(payload, "timestamp");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInteger(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out var value)
            ? value
            : null;

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string>? ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private sealed record PendingEvent(
        Guid Id,
        long ProjectId,
        string Fingerprint,
        string Title,
        string? Culprit,
        string Level,
        string Platform,
        string? Environment,
        string? Release,
        string? UserId,
        string Endpoint,
        DateTimeOffset ReceivedAt,
        DateTimeOffset Timestamp,
        string? Message,
        string PayloadJson);

    private sealed record QueuedEvent(PendingEvent Event, long IssueId);

    private sealed class InsertedEventRow
    {
        public Guid Id { get; set; }

        public long IssueId { get; set; }
    }
}
