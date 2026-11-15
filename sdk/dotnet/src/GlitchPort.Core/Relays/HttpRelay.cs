using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace GlitchPort
{
    /// <summary>
    /// Sends captured events to a glitch-port relay with
    /// <c>POST /api/{projectId}/events</c>. Bodies of 1024 UTF-8 bytes or more
    /// are gzip-compressed. Delivery happens on the base class worker thread; a
    /// failed attempt is retried once before it is counted as failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see cref="HttpRelayOptions.OfflineQueue"/> names a directory, events
    /// that cannot be delivered (network errors, timeouts, <c>5xx</c>,
    /// <c>429</c> and <c>503</c>) are written there verbatim and resent later by
    /// a background retry thread, on <see cref="QueuedRelay.FlushBuffer"/> and
    /// after every successful direct delivery. Other <c>4xx</c> responses are
    /// permanent: the event is reported as failed and never queued.
    /// </para>
    /// <para>
    /// A <c>Retry-After</c> header on <c>429</c>/<c>503</c> suspends all relay
    /// traffic until it expires.
    /// </para>
    /// </remarks>
    public sealed class HttpRelay : QueuedRelay
    {
        private const string TypeName = "http";
        private const string DefaultName = "http";
        private const string ApiKeyHeaderName = "X-GlitchPort-Key";
        private const int MaxAttempts = 2;
        private const int RetryDelayMilliseconds = 200;

        /// <summary>
        /// The minimum serialized body size, in UTF-8 bytes, at which the relay
        /// compresses the request. Smaller bodies are sent as-is.
        /// </summary>
        internal const int CompressionThresholdBytes = 1024;

        /// <summary>
        /// The hold applied to a <c>429</c>/<c>503</c> drain response whose
        /// <c>Retry-After</c> header is missing or invalid.
        /// </summary>
        private static readonly TimeSpan DefaultRateLimitHold = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The longest wait the retry thread performs before re-checking for a
        /// newly recorded <c>Retry-After</c> hold, so it can wake early.
        /// </summary>
        private static readonly TimeSpan RetryWaitChunk = TimeSpan.FromSeconds(1);

        /// <summary>An upper bound for parsed <c>Retry-After</c> delays.</summary>
        private static readonly TimeSpan MaxRateLimitHold = TimeSpan.FromDays(1);

        private const int RetryThreadShutdownTimeoutMilliseconds = 1000;

        private readonly HttpClient _httpClient;
        private readonly Uri _eventsEndpoint;
        private readonly string _publicKey;
        private readonly OfflineQueue? _offlineQueue;
        private readonly int _retryIntervalSeconds;
        private readonly object _drainGate = new object();
        private readonly ManualResetEventSlim _retryStop = new ManualResetEventSlim(false);

        private Thread? _retryThread;
        private long _retryAfterHoldUtcTicks;
        private int _disposed;

        /// <summary>Creates an HTTP relay.</summary>
        /// <param name="dsn">The parsed DSN that identifies the relay and project.</param>
        /// <param name="options">The relay options.</param>
        /// <param name="httpClient">The shared <see cref="HttpClient"/> used to send events.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The offline queue is configured and one of its limits is invalid.
        /// </exception>
        public HttpRelay(Dsn dsn, HttpRelayOptions options, HttpClient httpClient)
            : base(
                options is null ? DefaultName : options.Name ?? DefaultName,
                TypeName,
                options is null ? Level.Debug : options.MinLevel,
                options is null ? 1000 : options.QueueCapacity)
        {
            if (dsn is null)
            {
                throw new ArgumentNullException(nameof(dsn));
            }

            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (httpClient is null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            _httpClient = httpClient;
            _publicKey = dsn.PublicKey;
            _eventsEndpoint = new Uri(
                dsn.RelayBaseUri,
                "/api/" + dsn.ProjectId.ToString(CultureInfo.InvariantCulture) + "/events");

            var offlineOptions = options.OfflineQueue;
            if (offlineOptions is null || string.IsNullOrWhiteSpace(offlineOptions.Directory))
            {
                return;
            }

            // Only a configured queue validates its numbers; with no directory the
            // relay behaves exactly as it always did.
            offlineOptions.Validate();
            _retryIntervalSeconds = offlineOptions.RetryIntervalSeconds;

            // The queue never throws for an unusable directory or a held lock: it
            // reports the problem instead, and the relay behaves as if the queue
            // were not configured.
            var queue = new OfflineQueue(offlineOptions.Directory!, offlineOptions.MaxEvents, offlineOptions.MaxAgeHours);
            _offlineQueue = queue;

            if (!queue.IsAvailable)
            {
                RecordLastError(queue.LastError ?? "The offline queue is not available.");
                return;
            }

            if (queue.Dropped > 0)
            {
                MarkDropped(queue.Dropped);
            }

            _retryThread = new Thread(RetryLoop)
            {
                IsBackground = true,
                Name = "glitchport http offline retry",
            };
            _retryThread.Start();
        }

        /// <inheritdoc/>
        protected override void Handle(CapturedEvent e)
        {
            string? lastError = null;
            var body = Encoding.UTF8.GetBytes(e.Json);

            // While a Retry-After hold is active no request may reach the relay,
            // so the event goes straight to the disk queue.
            if (IsOfflineQueueAvailable && IsRateLimitHoldActive())
            {
                Persist(e, "The relay is rate limited; the event was queued.");
                return;
            }

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    Thread.Sleep(RetryDelayMilliseconds);

                    // Another drain may have observed a Retry-After hold while this
                    // attempt was sleeping; no request may be sent during it.
                    if (IsOfflineQueueAvailable && IsRateLimitHoldActive())
                    {
                        Persist(e, lastError ?? "The relay is rate limited; the event was queued.");
                        return;
                    }
                }

                var outcome = SendOnce(body);

                if (outcome.State == SendOutcomeState.Success)
                {
                    MarkDelivered();
                    DrainOfflineQueue();
                    return;
                }

                lastError = outcome.Error;

                if (outcome.State == SendOutcomeState.Permanent)
                {
                    // A permanent 4xx will never succeed: report it and move on
                    // instead of retrying or writing it to the offline queue.
                    MarkFailed(lastError);
                    return;
                }

                if (IsOfflineQueueAvailable
                    && outcome.IsThrottled
                    && outcome.HasRetryAfter
                    && outcome.RetryAfter > TimeSpan.FromMilliseconds(RetryDelayMilliseconds))
                {
                    // The relay asked us to back off longer than the immediate
                    // retry pause, so queue the event and wait it out.
                    SetRateLimitHold(outcome.RetryAfter);
                    Persist(e, lastError);
                    return;
                }
            }

            if (IsOfflineQueueAvailable)
            {
                Persist(e, lastError ?? "The relay request failed.");
            }
            else
            {
                MarkFailed(lastError ?? "The relay request failed.");
            }
        }

        /// <summary>
        /// Drains the offline queue from <see cref="QueuedRelay.FlushBuffer"/>, so
        /// <see cref="QueuedRelay.FlushAsync"/> and dispose also flush the backlog.
        /// </summary>
        protected override void FlushBuffer()
        {
            DrainOfflineQueue();
        }

        /// <inheritdoc/>
        protected override long GetQueuedCount()
        {
            var queue = _offlineQueue;
            return queue is null ? 0 : queue.Count;
        }

        /// <summary>
        /// Stops the retry thread and releases the offline queue. The base worker
        /// performs its final drain while the queue is still alive; a late drain
        /// after the queue was disposed is a no-op.
        /// </summary>
        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var retryThreadExited = true;

            try
            {
                _retryStop.Set();
                var retryThread = _retryThread;
                if (retryThread is not null)
                {
                    try
                    {
                        if (retryThread != Thread.CurrentThread)
                        {
                            retryThreadExited = retryThread.Join(RetryThreadShutdownTimeoutMilliseconds);
                        }
                    }
                    catch (ThreadStateException)
                    {
                        // The retry thread already finished.
                    }
                }
            }
            finally
            {
                // The worker drains the offline queue in its final FlushBuffer, so
                // the queue must outlive the base dispose.
                try
                {
                    base.Dispose();
                }
                finally
                {
                    var queue = _offlineQueue;
                    queue?.Dispose();

                    // A retry thread that is still alive (a hung handler) would
                    // observe an ObjectDisposedException on the stop event; its
                    // background status means it never keeps the process alive.
                    if (retryThreadExited)
                    {
                        _retryStop.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Sends one serialized event and classifies the result. Never throws:
        /// network failures become transient outcomes.
        /// </summary>
        private SendOutcome SendOnce(byte[] body)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _eventsEndpoint))
                {
                    request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, _publicKey);
                    request.Content = CreateContent(body);

                    using (var response = _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseContentRead)
                        .GetAwaiter()
                        .GetResult())
                    {
                        var responseBody = response.Content is null
                            ? null
                            : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                        if (response.IsSuccessStatusCode)
                        {
                            return SendOutcome.Success;
                        }

                        var status = (int)response.StatusCode;
                        var error = DescribeStatus(response.StatusCode, responseBody);

                        if (status == (int)HttpStatusCode.TooManyRequests
                            || status == (int)HttpStatusCode.ServiceUnavailable)
                        {
                            var hasRetryAfter = TryGetRetryAfter(response, out var retryAfter);
                            return SendOutcome.Transient(error, isThrottled: true, hasRetryAfter, retryAfter);
                        }

                        if (status >= 500 || status < 400)
                        {
                            return SendOutcome.Transient(error);
                        }

                        return SendOutcome.Permanent(error);
                    }
                }
            }
            catch (Exception ex)
            {
                return SendOutcome.Transient(RelayErrorText.Describe(ex));
            }
        }

        /// <summary>
        /// Sends every queued event, oldest first, until the queue is empty, a
        /// transient error asks for a pause, or a Retry-After hold is active.
        /// </summary>
        private void DrainOfflineQueue()
        {
            var queue = _offlineQueue;
            if (queue is null)
            {
                return;
            }

            // One drain at a time: the worker, the retry thread and FlushAsync must
            // never send the same file twice.
            lock (_drainGate)
            {
                if (!queue.IsAvailable)
                {
                    return;
                }

                var evicted = queue.EnforceLimits();
                if (evicted > 0)
                {
                    MarkDropped(evicted);
                }

                // A file that cannot be removed would be resent forever; remembering
                // what this pass already sent keeps the loop bounded.
                HashSet<string>? attempted = null;

                while (!IsRateLimitHoldActive())
                {
                    if (!queue.TryPeekOldest(out var entry) || entry is null)
                    {
                        return;
                    }

                    attempted ??= new HashSet<string>(StringComparer.Ordinal);
                    if (!attempted.Add(entry.Path))
                    {
                        return;
                    }

                    var outcome = SendOnce(Encoding.UTF8.GetBytes(entry.Json));

                    if (outcome.State == SendOutcomeState.Success)
                    {
                        queue.Remove(entry);
                        MarkDelivered();
                        continue;
                    }

                    if (outcome.State == SendOutcomeState.Permanent)
                    {
                        queue.Remove(entry);
                        MarkFailed(outcome.Error);
                        continue;
                    }

                    // A transient failure stops the pass but never counts the event
                    // as failed: it stays on disk for the next cycle.
                    RecordLastError(outcome.Error);

                    if (outcome.IsThrottled)
                    {
                        var hold = outcome.HasRetryAfter && outcome.RetryAfter > TimeSpan.Zero
                            ? outcome.RetryAfter
                            : DefaultRateLimitHold;
                        SetRateLimitHold(hold);
                    }

                    return;
                }
            }
        }

        /// <summary>
        /// Runs on the dedicated retry thread: drains immediately, then once per
        /// retry interval, waking early when a Retry-After hold expires.
        /// </summary>
        private void RetryLoop()
        {
            var nextDrainUtc = DateTime.UtcNow;

            while (!_retryStop.IsSet)
            {
                var now = DateTime.UtcNow;
                var target = nextDrainUtc;
                var holdTicks = Interlocked.Read(ref _retryAfterHoldUtcTicks);

                if (holdTicks > now.Ticks && holdTicks < target.Ticks)
                {
                    target = new DateTime(holdTicks, DateTimeKind.Utc);
                }

                var remaining = target - now;
                if (remaining > TimeSpan.Zero)
                {
                    var chunk = remaining < RetryWaitChunk ? remaining : RetryWaitChunk;
                    if (_retryStop.Wait(chunk))
                    {
                        return;
                    }

                    continue;
                }

                try
                {
                    DrainOfflineQueue();
                }
                catch (Exception ex)
                {
                    RecordLastError(RelayErrorText.Describe(ex));
                }

                var wait = TimeSpan.FromSeconds(_retryIntervalSeconds);
                var holdRemaining = GetRateLimitHoldRemaining();
                if (holdRemaining.HasValue && holdRemaining.Value < wait)
                {
                    wait = holdRemaining.Value;
                }

                nextDrainUtc = DateTime.UtcNow.Add(wait > TimeSpan.Zero ? wait : RetryWaitChunk);
            }
        }

        /// <summary>Writes one event to the disk queue and counts what the write did.</summary>
        private void Persist(CapturedEvent e, string sendError)
        {
            var queue = _offlineQueue;
            if (queue is null)
            {
                MarkFailed(sendError);
                return;
            }

            if (queue.TryEnqueue(e.Json, e.EventId, e.Timestamp, out var evicted, out var error))
            {
                // A persisted event counts as queued, never as failed.
                if (evicted > 0)
                {
                    MarkDropped(evicted);
                }

                return;
            }

            MarkFailed(string.IsNullOrWhiteSpace(error) ? sendError : error!);
        }

        /// <summary>Gets a value indicating whether the disk queue exists and owns its directory.</summary>
        private bool IsOfflineQueueAvailable
        {
            get
            {
                var queue = _offlineQueue;
                return queue is not null && queue.IsAvailable;
            }
        }

        private bool IsRateLimitHoldActive()
        {
            return GetRateLimitHoldRemaining().HasValue;
        }

        private TimeSpan? GetRateLimitHoldRemaining()
        {
            var ticks = Interlocked.Read(ref _retryAfterHoldUtcTicks);
            if (ticks <= 0)
            {
                return null;
            }

            var remaining = ticks - DateTime.UtcNow.Ticks;
            return remaining > 0 ? TimeSpan.FromTicks(remaining) : (TimeSpan?)null;
        }

        private void SetRateLimitHold(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            if (delay > MaxRateLimitHold)
            {
                delay = MaxRateLimitHold;
            }

            Interlocked.Exchange(ref _retryAfterHoldUtcTicks, DateTime.UtcNow.Add(delay).Ticks);
        }

        /// <summary>
        /// Reads <c>Retry-After</c> as delta-seconds or an HTTP date. A missing or
        /// malformed header yields <see langword="false"/>.
        /// </summary>
        private static bool TryGetRetryAfter(HttpResponseMessage response, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;

            RetryConditionHeaderValue? retryAfter;
            try
            {
                retryAfter = response.Headers.RetryAfter;
            }
            catch (FormatException)
            {
                return false;
            }

            if (retryAfter is null)
            {
                return false;
            }

            if (retryAfter.Delta.HasValue)
            {
                delay = retryAfter.Delta.Value;
                return true;
            }

            if (retryAfter.Date.HasValue)
            {
                delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Creates the request content for one attempt. Large payloads are
        /// compressed with gzip and advertised with <c>Content-Encoding: gzip</c>.
        /// </summary>
        private static HttpContent CreateContent(byte[] body)
        {
            HttpContent content;

            if (body.Length >= CompressionThresholdBytes)
            {
                using (var buffer = new MemoryStream())
                {
                    using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        gzip.Write(body, 0, body.Length);
                    }

                    content = new ByteArrayContent(buffer.ToArray());
                }

                content.Headers.ContentEncoding.Add("gzip");
            }
            else
            {
                content = new ByteArrayContent(body);
            }

            content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

            return content;
        }

        private static string DescribeStatus(HttpStatusCode statusCode, string? responseBody)
        {
            var status = "HTTP "
                + ((int)statusCode).ToString(CultureInfo.InvariantCulture)
                + " ("
                + statusCode
                + ")";

            var body = RelayErrorText.Sanitize(responseBody);
            return body.Length == 0 ? status : status + ": " + body;
        }

        private enum SendOutcomeState
        {
            Success,
            Transient,
            Permanent,
        }

        /// <summary>The result of one HTTP attempt.</summary>
        private sealed class SendOutcome
        {
            private SendOutcome(
                SendOutcomeState state,
                string error,
                bool isThrottled,
                bool hasRetryAfter,
                TimeSpan retryAfter)
            {
                State = state;
                Error = error;
                IsThrottled = isThrottled;
                HasRetryAfter = hasRetryAfter;
                RetryAfter = retryAfter;
            }

            public static SendOutcome Success { get; } = new SendOutcome(
                SendOutcomeState.Success,
                string.Empty,
                isThrottled: false,
                hasRetryAfter: false,
                retryAfter: TimeSpan.Zero);

            public SendOutcomeState State { get; }

            public string Error { get; }

            /// <summary>Gets a value indicating whether the response was a 429 or 503.</summary>
            public bool IsThrottled { get; }

            /// <summary>Gets a value indicating whether a Retry-After header was parsed.</summary>
            public bool HasRetryAfter { get; }

            /// <summary>Gets the parsed Retry-After delay; may be non-positive.</summary>
            public TimeSpan RetryAfter { get; }

            public static SendOutcome Transient(
                string error,
                bool isThrottled = false,
                bool hasRetryAfter = false,
                TimeSpan retryAfter = default)
            {
                return new SendOutcome(SendOutcomeState.Transient, error, isThrottled, hasRetryAfter, retryAfter);
            }

            public static SendOutcome Permanent(string error)
            {
                return new SendOutcome(
                    SendOutcomeState.Permanent,
                    error,
                    isThrottled: false,
                    hasRetryAfter: false,
                    retryAfter: TimeSpan.Zero);
            }
        }
    }
}
