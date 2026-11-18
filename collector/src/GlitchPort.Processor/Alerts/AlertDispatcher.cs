using System.Globalization;
using System.Net;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GlitchPort.Processor.Alerts;

/// <summary>Outcome of one HTTP delivery attempt.</summary>
/// <param name="Delivered">Whether the target answered with a 2xx status code.</param>
/// <param name="StatusCode">HTTP status code, or <c>null</c> when the request failed before a response.</param>
/// <param name="Error">Failure text (status code or exception message, at most 500 characters).</param>
/// <param name="RetryAfterSeconds">Delay requested by a 429 <c>Retry-After</c> header, if any.</param>
public sealed record AlertDeliveryResult(
    bool Delivered,
    int? StatusCode,
    string? Error,
    int? RetryAfterSeconds);

/// <summary>Counters of one dispatch run.</summary>
/// <param name="Claimed">Number of notifications claimed by the run.</param>
/// <param name="Delivered">Number of notifications marked sent.</param>
/// <param name="Retried">Number of notifications rescheduled.</param>
/// <param name="Failed">Number of notifications marked failed.</param>
public sealed record AlertDispatchResult(int Claimed, int Delivered, int Retried, int Failed);

/// <summary>
/// Claims due alert notifications and delivers them over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// The claim is one transaction: up to <see cref="MaxBatchSize"/> due rows are
/// selected with <c>for update skip locked</c>, marked <c>sending</c> with a
/// two minute lease and their attempt counter is incremented. The transaction
/// commits before the HTTP request starts, so no database lock is held while a
/// target is slow, and two dispatchers can never claim (and therefore never
/// send) the same row.
/// </para>
/// <para>
/// A 2xx response marks the notification <c>sent</c>. Any other result
/// reschedules it with an exponential backoff
/// (<c>min(2^attempts * 30s, 1h)</c>) while the attempt counter is below
/// <see cref="AlertOptions.MaxAttempts"/>, and marks it <c>failed</c>
/// afterwards. A 429 response honours its <c>Retry-After</c> header (seconds or
/// HTTP date). <c>last_error</c> is truncated to 500 characters.
/// </para>
/// </remarks>
public sealed class AlertDispatcher
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client used for deliveries.</summary>
    public const string HttpClientName = "alerts";

    /// <summary>Maximum number of notifications claimed by one dispatch run.</summary>
    public const int MaxBatchSize = 20;

    /// <summary>Maximum length of the stored <c>last_error</c> text.</summary>
    public const int MaxErrorLength = 500;

    private const string ClaimSql = """
        with due as (
            select n.id,
                   n.rule_id
            from alert_notifications n
            where n.status in ('pending', 'sending')
              and n.next_attempt_at <= now()
              and (n.locked_until is null or n.locked_until < now())
            order by n.id
            limit @Limit
            for update skip locked
        )
        update alert_notifications n
        set status = 'sending',
            locked_until = now() + interval '2 minutes',
            attempts = n.attempts + 1
        from due
        join alert_rules r on r.id = due.rule_id
        where n.id = due.id
        returning n.id as "Id",
                  n.project_id as "ProjectId",
                  n.issue_id as "IssueId",
                  n.trigger as "Trigger",
                  n.payload::text as "PayloadJson",
                  n.attempts as "Attempts",
                  r.channel as "Channel",
                  r.target_url as "TargetUrl"
        """;

    private const string MarkSentSql = """
        update alert_notifications
        set status = 'sent',
            sent_at = now(),
            last_error = null,
            locked_until = null
        where id = @Id
        """;

    private const string MarkRetrySql = """
        update alert_notifications
        set status = 'pending',
            next_attempt_at = @NextAttemptAt,
            last_error = @LastError,
            locked_until = null
        where id = @Id
        """;

    private const string MarkFailedSql = """
        update alert_notifications
        set status = 'failed',
            last_error = @LastError,
            locked_until = null
        where id = @Id
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<AlertOptions> _options;
    private readonly ILogger<AlertDispatcher> _logger;

    /// <summary>
    /// Creates a dispatcher that claims notifications through
    /// <paramref name="dataSource"/> and sends them with the named
    /// <see cref="HttpClientName"/> client.
    /// </summary>
    public AlertDispatcher(
        NpgsqlDataSource dataSource,
        IHttpClientFactory httpClientFactory,
        IOptions<AlertOptions> options,
        ILogger<AlertDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Claims the due notifications and delivers them.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the run.</param>
    /// <returns>The counters of the run.</returns>
    public async Task<AlertDispatchResult> DispatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimed = await ClaimAsync(connection, cancellationToken).ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return new AlertDispatchResult(0, 0, 0, 0);
        }

        var delivered = 0;
        var retried = 0;
        var failed = 0;
        var maxAttempts = _options.Value.MaxAttempts;

        foreach (var notification in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payload = AlertPayloads.Deserialize(notification.PayloadJson);
            if (payload is null)
            {
                await MarkFailedAsync(
                        connection,
                        notification.Id,
                        "The stored payload is not a valid alert payload.",
                        cancellationToken)
                    .ConfigureAwait(false);
                failed++;
                continue;
            }

            var result = await SendAsync(
                    notification.Channel,
                    notification.TargetUrl,
                    payload,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Delivered)
            {
                await connection
                    .ExecuteAsync(new CommandDefinition(
                        MarkSentSql,
                        new { notification.Id },
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                delivered++;
                continue;
            }

            if (notification.Attempts < maxAttempts)
            {
                var delay = RetryDelay(result, notification.Attempts);
                await connection
                    .ExecuteAsync(new CommandDefinition(
                        MarkRetrySql,
                        new
                        {
                            notification.Id,
                            NextAttemptAt = DateTimeOffset.UtcNow.Add(delay),
                            LastError = result.Error,
                        },
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                retried++;
                continue;
            }

            await MarkFailedAsync(connection, notification.Id, result.Error, cancellationToken)
                .ConfigureAwait(false);
            failed++;
        }

        if (delivered > 0 || retried > 0 || failed > 0)
        {
            _logger.LogInformation(
                "Alert dispatch finished (Claimed {Claimed}, Delivered {Delivered}, Retried {Retried}, Failed {Failed}).",
                claimed.Count,
                delivered,
                retried,
                failed);
        }

        return new AlertDispatchResult(claimed.Count, delivered, retried, failed);
    }

    /// <summary>
    /// Formats <paramref name="payload"/> for <paramref name="channel"/> and
    /// posts it to <paramref name="targetUrl"/> once. Used by the dispatcher
    /// and by the management test endpoint, which never touches the queue.
    /// </summary>
    public async Task<AlertDeliveryResult> SendAsync(
        string channel,
        string targetUrl,
        AlertPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string body;
        try
        {
            body = AlertFormatters.Format(channel, payload);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AlertDeliveryResult(false, null, Truncate(exception.Message), null);
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new AlertDeliveryResult(true, (int)response.StatusCode, null, null);
            }

            var error = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            var retryAfter = response.StatusCode == HttpStatusCode.TooManyRequests
                ? ReadRetryAfter(response)
                : null;

            return new AlertDeliveryResult(false, (int)response.StatusCode, error, retryAfter);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AlertDeliveryResult(false, null, Truncate(exception.Message), null);
        }
    }

    /// <summary>
    /// Computes how long a failed attempt waits before the next one: the
    /// <c>Retry-After</c> delay of a 429 response when present, otherwise
    /// <c>min(2^attempts * 30s, 1h)</c>.
    /// </summary>
    /// <param name="result">The failed attempt.</param>
    /// <param name="attempts">Attempt counter after the failed attempt.</param>
    public static TimeSpan RetryDelay(AlertDeliveryResult result, int attempts)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.RetryAfterSeconds is { } retryAfterSeconds)
        {
            return TimeSpan.FromSeconds(Math.Max(retryAfterSeconds, 0));
        }

        var seconds = Math.Min(Math.Pow(2, Math.Max(attempts, 1)) * 30d, 3600d);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Truncates failure text to <see cref="MaxErrorLength"/> characters.</summary>
    public static string? Truncate(string? message)
    {
        if (message is null)
        {
            return null;
        }

        return message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
    }

    private static int? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return (int)Math.Max(Math.Ceiling(delta.TotalSeconds), 0);
        }

        if (retryAfter.Date is { } date)
        {
            return (int)Math.Max(Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds), 0);
        }

        return null;
    }

    private static async Task<List<ClaimedNotification>> ClaimAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var claimed = (await connection
                    .QueryAsync<ClaimedNotification>(new CommandDefinition(
                        ClaimSql,
                        new { Limit = MaxBatchSize },
                        transaction,
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false))
                .ToList();

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claimed;
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static Task<int> MarkFailedAsync(
        NpgsqlConnection connection,
        long id,
        string? error,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            MarkFailedSql,
            new { Id = id, LastError = error },
            cancellationToken: cancellationToken));

    private static async Task RollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // The connection is already unusable; PostgreSQL rolls back on
            // disconnect regardless.
        }
    }

    private sealed class ClaimedNotification
    {
        public long Id { get; init; }

        public long ProjectId { get; init; }

        public long? IssueId { get; init; }

        public string Trigger { get; init; } = string.Empty;

        public string PayloadJson { get; init; } = "{}";

        public int Attempts { get; init; }

        public string Channel { get; init; } = string.Empty;

        public string TargetUrl { get; init; } = string.Empty;
    }
}
