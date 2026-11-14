using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort
{
    /// <summary>
    /// Captures messages and fans them out to independent relays. Capture only
    /// enqueues the event and returns immediately; delivery errors are reported by
    /// <see cref="FlushAsync"/> instead of thrown from capture.
    /// </summary>
    public sealed class GlitchPortClient : IDisposable
    {
        private const string FallbackSdkVersion = "0.0.0";

        private static readonly string InformationalVersion = ResolveInformationalVersion();
        private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(2);

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly string _sdkName;
        private readonly string _sdkVersion;
        private readonly RelayFanout _fanout;
        private readonly EventHandler _processExitHandler;

        private int _disposeState;

        /// <summary>Creates a client for the configured relays.</summary>
        /// <param name="options">
        /// The client options. At least one relay must be configured: either
        /// <see cref="GlitchPortOptions.Dsn"/> or an enabled
        /// <see cref="GlitchPortOptions.Relays"/> entry.
        /// </param>
        /// <param name="httpClient">
        /// An optional <see cref="HttpClient"/> shared by every HTTP relay. When
        /// supplied, the client uses it and never disposes it; otherwise the client
        /// creates and owns a private instance.
        /// </param>
        /// <exception cref="ArgumentException">No relay is configured.</exception>
        public GlitchPortClient(GlitchPortOptions options, HttpClient? httpClient = null)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _sdkName = string.IsNullOrWhiteSpace(options.SdkName) ? GlitchPortOptions.DefaultSdkName : options.SdkName;
            _sdkVersion = string.IsNullOrWhiteSpace(options.SdkVersion) ? InformationalVersion : options.SdkVersion;

            if (httpClient is null)
            {
                _httpClient = new HttpClient();
                _ownsHttpClient = true;
            }
            else
            {
                _httpClient = httpClient;
                _ownsHttpClient = false;
            }

            _fanout = CreateFanout(options);

            // The handler uses the same one-shot cleanup as Dispose so the last
            // queued events have a chance to reach their relays at process exit.
            _processExitHandler = OnProcessExit;
            AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        }

        /// <summary>Gets the relay instances created for this client, in fan-out order.</summary>
        public IReadOnlyList<IRelay> Relays => _fanout.Relays;

        /// <summary>Captures a message at <see cref="Level.Info"/>.</summary>
        /// <param name="message">The message text.</param>
        /// <param name="ct">A token that cancels the capture before it is queued.</param>
        /// <returns>The generated event id.</returns>
        public Task<Guid> CaptureMessageAsync(string message, CancellationToken ct = default)
        {
            return CaptureMessageAsync(message, Level.Info, ct);
        }

        /// <summary>
        /// Captures a message at the given level. The returned task completes as
        /// soon as the event is queued; it never waits for the network.
        /// </summary>
        /// <param name="message">The message text.</param>
        /// <param name="level">The event severity.</param>
        /// <param name="ct">A token that cancels the capture before it is queued.</param>
        /// <returns>The generated event id.</returns>
        public Task<Guid> CaptureMessageAsync(string message, Level level, CancellationToken ct = default)
        {
            if (message is null)
            {
                return Task.FromException<Guid>(new ArgumentNullException(nameof(message)));
            }

            if (Volatile.Read(ref _disposeState) != 0)
            {
                return Task.FromException<Guid>(new ObjectDisposedException(nameof(GlitchPortClient)));
            }

            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled<Guid>(ct);
            }

            var eventId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow;
            var payload = JsonPayloadWriter.CreateMessagePayload(
                eventId,
                timestamp,
                level,
                message,
                _sdkName,
                _sdkVersion);

            var capturedEvent = new CapturedEvent(
                eventId,
                timestamp,
                level,
                message,
                _sdkName,
                _sdkVersion,
                payload);

            _fanout.Enqueue(capturedEvent);

            return Task.FromResult(eventId);
        }

        /// <summary>
        /// Flushes every relay and returns one delivery result per relay.
        /// </summary>
        /// <param name="timeout">The flush timeout per relay. Defaults to 5 seconds.</param>
        /// <returns>The combined delivery report.</returns>
        public Task<DeliveryReport> FlushAsync(TimeSpan? timeout = null)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return Task.FromException<DeliveryReport>(new ObjectDisposedException(nameof(GlitchPortClient)));
            }

            return _fanout.FlushAsync(timeout ?? DefaultFlushTimeout);
        }

        /// <summary>
        /// Flushes for at most two seconds, then disposes every relay. An injected
        /// <see cref="HttpClient"/> is left untouched. The method is idempotent and
        /// never throws.
        /// </summary>
        public void Dispose()
        {
            DisposeCore();
            GC.SuppressFinalize(this);
        }

        private RelayFanout CreateFanout(GlitchPortOptions options)
        {
            var context = new RelayCreationContext(_httpClient, _sdkName, _sdkVersion);
            var relays = new List<IRelay>();

            try
            {
                if (options.Dsn is not null)
                {
                    // The implicit relay is built straight from the parsed DSN; no
                    // string round-trip and no chance of a different parse result.
                    var implicitRelay = new HttpRelayDefinition(options.Dsn, new HttpRelayOptions());
                    relays.Add(implicitRelay.CreateRelay(context));
                }

                for (var i = 0; i < options.Relays.Count; i++)
                {
                    var definition = options.Relays[i];
                    if (definition is null)
                    {
                        throw new ArgumentException("Relay definitions must not be null.", nameof(options));
                    }

                    if (!definition.Enabled)
                    {
                        continue;
                    }

                    relays.Add(definition.CreateRelay(context));
                }

                if (relays.Count == 0)
                {
                    throw new ArgumentException(
                        "At least one relay must be configured: set Dsn or add an enabled entry to Relays.",
                        nameof(options));
                }

                return new RelayFanout(relays);
            }
            catch (Exception)
            {
                for (var i = 0; i < relays.Count; i++)
                {
                    try
                    {
                        relays[i].Dispose();
                    }
                    catch (Exception)
                    {
                        // Keep unwinding: the constructor is about to throw anyway.
                    }
                }

                if (_ownsHttpClient)
                {
                    _httpClient.Dispose();
                }

                throw;
            }
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            DisposeCore();
        }

        private void DisposeCore()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            try
            {
                AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
            }
            catch (Exception)
            {
                // The process is already exiting; the subscription no longer matters.
            }

            try
            {
                // Wait returns even when the flush task cannot complete (for example
                // because timers are already gone during process shutdown).
                _fanout
                    .FlushAsync(DisposeFlushTimeout)
                    .Wait(DisposeFlushTimeout + TimeSpan.FromMilliseconds(250));
            }
            catch (Exception)
            {
                // Dispose must never throw, even while the process is shutting down.
            }

            try
            {
                _fanout.Dispose();
            }
            catch (Exception)
            {
                // QueuedRelay.Dispose is best effort; keep going and release the client.
            }

            if (_ownsHttpClient)
            {
                try
                {
                    _httpClient.Dispose();
                }
                catch (Exception)
                {
                    // Nothing useful can be done with a failing dispose.
                }
            }
        }

        private static string ResolveInformationalVersion()
        {
            var informationalVersion = typeof(GlitchPortClient).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            return string.IsNullOrWhiteSpace(informationalVersion) ? FallbackSdkVersion : informationalVersion;
        }
    }
}
