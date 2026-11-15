using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.Tests
{
    /// <summary>Builds <see cref="CapturedEvent"/> instances for relay tests.</summary>
    internal static class RelayTestData
    {
        public static CapturedEvent CreateEvent(Level level = Level.Info, string message = "hello")
        {
            var eventId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;

            // The relay tests only need the JSON to round-trip unchanged; building it
            // by hand keeps the test independent of the internal payload writer.
            var json = "{\"event_id\":\"" + eventId.ToString("D") + "\",\"message\":\"" + message + "\"}";

            return new CapturedEvent(
                eventId,
                timestamp,
                level,
                message,
                GlitchPortOptions.DefaultSdkName,
                "1.2.3",
                json);
        }
    }

    /// <summary>A scriptable <see cref="IRelay"/> used for fan-out tests.</summary>
    internal sealed class FakeRelay : IRelay
    {
        private readonly object _eventsGate = new object();
        private readonly List<CapturedEvent> _events = new List<CapturedEvent>();

        public FakeRelay(string name, string type = "fake", Level minLevel = Level.Debug)
        {
            Name = name;
            Type = type;
            MinLevel = minLevel;
        }

        public string Name { get; }

        public string Type { get; }

        public Level MinLevel { get; }

        public bool Accept { get; set; } = true;

        public bool ThrowOnEnqueue { get; set; }

        public bool ThrowOnFlush { get; set; }

        public bool Disposed { get; private set; }

        public Func<TimeSpan, Task<RelayDeliveryResult>>? FlushHandler { get; set; }

        public IReadOnlyList<CapturedEvent> Events
        {
            get
            {
                lock (_eventsGate)
                {
                    return _events.ToArray();
                }
            }
        }

        public bool TryEnqueue(CapturedEvent e)
        {
            if (ThrowOnEnqueue)
            {
                throw new InvalidOperationException("enqueue failure");
            }

            lock (_eventsGate)
            {
                _events.Add(e);
                return Accept;
            }
        }

        public Task<RelayDeliveryResult> FlushAsync(TimeSpan timeout)
        {
            if (ThrowOnFlush)
            {
                throw new InvalidOperationException("flush failure");
            }

            if (FlushHandler is not null)
            {
                return FlushHandler(timeout);
            }

            return Task.FromResult(new RelayDeliveryResult(Name, Type, Events.Count, 0, 0, null));
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that records the requests it sees and
    /// delegates the response to a callback keyed by attempt number.
    /// </summary>
    internal sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _responder;
        private int _attempts;

        public TestHttpMessageHandler(Func<int, HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        public int Attempts => Volatile.Read(ref _attempts);

        public HttpMethod? LastMethod { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public string? LastApiKey { get; private set; }

        public string? LastContentType { get; private set; }

        public string? LastBody { get; private set; }

        public static HttpResponseMessage Response(HttpStatusCode statusCode, string body = "{}")
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            cancellationToken.ThrowIfCancellationRequested();

            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastApiKey = request.Headers.TryGetValues("X-GlitchPort-Key", out var values)
                ? string.Join(",", values)
                : null;
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

            return _responder(attempt, request);
        }
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> whose response can be switched at
    /// runtime and which records, in order, the body of every request answered
    /// with a success status.
    /// </summary>
    internal sealed class ScriptableHttpMessageHandler : HttpMessageHandler
    {
        private readonly object _gate = new object();
        private readonly List<string> _successfulBodies = new List<string>();
        private Func<int, HttpRequestMessage, HttpResponseMessage> _responder;
        private int _attempts;

        public ScriptableHttpMessageHandler(Func<int, HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>Gets the request bodies answered with a success status, in request order.</summary>
        public IReadOnlyList<string> SuccessfulBodies
        {
            get
            {
                lock (_gate)
                {
                    return _successfulBodies.ToArray();
                }
            }
        }

        /// <summary>Replaces the responder used for every later request.</summary>
        public void Respond(Func<int, HttpRequestMessage, HttpResponseMessage> responder)
        {
            lock (_gate)
            {
                _responder = responder ?? throw new ArgumentNullException(nameof(responder));
            }
        }

        /// <summary>Answers every later request with <paramref name="statusCode"/>.</summary>
        public void Respond(HttpStatusCode statusCode, string body = "{}")
        {
            Respond((_, _) => TestHttpMessageHandler.Response(statusCode, body));
        }

        /// <summary>Adds a <c>Retry-After</c> header in delta-seconds to a response.</summary>
        public static HttpResponseMessage WithRetryAfter(HttpResponseMessage response, TimeSpan delay)
        {
            if (response is null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
            return response;
        }

        /// <summary>Adds a <c>Retry-After</c> header as an HTTP date to a response.</summary>
        public static HttpResponseMessage WithRetryAfterDate(HttpResponseMessage response, DateTimeOffset date)
        {
            if (response is null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(date);
            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            cancellationToken.ThrowIfCancellationRequested();

            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

            Func<int, HttpRequestMessage, HttpResponseMessage> responder;
            lock (_gate)
            {
                responder = _responder;
            }

            var response = responder(attempt, request);

            if (response.IsSuccessStatusCode && body is not null)
            {
                lock (_gate)
                {
                    _successfulBodies.Add(body);
                }
            }

            return response;
        }
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> whose request stays in flight until the
    /// test releases it, so tests can prove that capture does not wait for the
    /// network.
    /// </summary>
    internal sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _started =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _release =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _attempts;

        public Task Started => _started.Task;

        public int Attempts => Volatile.Read(ref _attempts);

        public void Release()
        {
            _release.TrySetResult(true);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            _started.TrySetResult(true);
            await _release.Task.ConfigureAwait(false);
            return TestHttpMessageHandler.Response(HttpStatusCode.OK);
        }
    }
}
