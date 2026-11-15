using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort
{
    /// <summary>
    /// Captures messages, exceptions and protocol v1 events and fans them out to
    /// independent relays. Capture only enqueues the event and returns
    /// immediately; delivery errors are reported by <see cref="FlushAsync"/>
    /// instead of thrown from capture.
    /// </summary>
    public sealed class GlitchPortClient : IDisposable
    {
        private const string FallbackSdkVersion = "0.0.0";
        private const string RuntimeContextName = "dotnet";

        private static readonly string InformationalVersion = ResolveInformationalVersion();
        private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(2);

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly string _sdkName;
        private readonly string _sdkVersion;
        private readonly GlitchPortOptions _options;
        private readonly RelayFanout _fanout;
        private readonly EventHandler _processExitHandler;
        private readonly IDisposable? _unhandledExceptionIntegration;
        private readonly Scope _globalScope = new Scope();
        private readonly AsyncLocal<Scope?> _currentScope = new AsyncLocal<Scope?>();
        private readonly Random _sampler = new Random();
        private readonly object _samplerGate = new object();

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

            _options = options;
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

            if (options.CaptureUnhandledExceptions)
            {
                _unhandledExceptionIntegration = UnhandledExceptionIntegration.Register(this);
            }
        }

        /// <summary>Gets the options this client was created with.</summary>
        public GlitchPortOptions Options => _options;

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

            var failure = GetCaptureFailure(ct);
            if (failure is not null)
            {
                return failure;
            }

            var e = new GlitchPortEvent
            {
                EventId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Level = level,
                Message = message,
                Platform = ResolveInitialPlatform(),
            };

            return Capture(e, levelFromCaptureApi: true);
        }

        /// <summary>
        /// Captures an exception and its inner exception chain. The returned task
        /// completes as soon as the event is queued; it never waits for the
        /// network.
        /// </summary>
        /// <param name="ex">The exception to report.</param>
        /// <param name="level">The event severity. Defaults to <see cref="Level.Error"/>.</param>
        /// <param name="ct">A token that cancels the capture before it is queued.</param>
        /// <returns>The generated event id.</returns>
        public Task<Guid> CaptureExceptionAsync(
            Exception ex,
            Level level = Level.Error,
            CancellationToken ct = default)
        {
            if (ex is null)
            {
                return Task.FromException<Guid>(new ArgumentNullException(nameof(ex)));
            }

            var failure = GetCaptureFailure(ct);
            if (failure is not null)
            {
                return failure;
            }

            var e = new GlitchPortEvent
            {
                EventId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Level = level,
                Platform = ResolveInitialPlatform(),
            };

            try
            {
                e.Exceptions = ExceptionParser.Parse(ex, _options);
            }
            catch (Exception)
            {
                // A malformed exception must not break capture; fall back to a
                // message so the event still has a required field.
                e.Message = DescribeException(ex);
            }

            return Capture(e, levelFromCaptureApi: true);
        }

        /// <summary>
        /// Captures a fully built protocol v1 event. An empty event id or
        /// timestamp is filled in before the event is queued.
        /// </summary>
        /// <param name="e">The event to report.</param>
        /// <param name="ct">A token that cancels the capture before it is queued.</param>
        /// <returns>The event id.</returns>
        public Task<Guid> CaptureEventAsync(GlitchPortEvent e, CancellationToken ct = default)
        {
            if (e is null)
            {
                return Task.FromException<Guid>(new ArgumentNullException(nameof(e)));
            }

            var failure = GetCaptureFailure(ct);
            if (failure is not null)
            {
                return failure;
            }

            if (e.EventId == Guid.Empty)
            {
                e.EventId = Guid.NewGuid();
            }

            if (e.Timestamp == default)
            {
                e.Timestamp = DateTimeOffset.UtcNow;
            }

            // The level is an event value here, so it wins over a scope level.
            return Capture(e, levelFromCaptureApi: false);
        }

        /// <summary>
        /// Configures the innermost pushed scope of the current asynchronous flow,
        /// or the global scope when no scope is pushed.
        /// </summary>
        /// <param name="configure">The callback that updates the scope.</param>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        public void ConfigureScope(Action<Scope> configure)
        {
            if (configure is null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            configure(_currentScope.Value ?? _globalScope);
        }

        /// <summary>
        /// Pushes a new scope for the current asynchronous flow and returns a
        /// disposable that restores the previous scope. Nested and parallel async
        /// flows each see their own scope; every scope is merged on top of the
        /// global scope.
        /// </summary>
        /// <returns>The disposable that pops the scope.</returns>
        public IDisposable PushScope()
        {
            var parent = _currentScope.Value;
            var scope = new Scope { Parent = parent };
            _currentScope.Value = scope;
            return new PushedScope(this, scope, parent);
        }

        /// <summary>Sets a tag on the current pushed scope, or on the global scope.</summary>
        /// <param name="key">The tag name.</param>
        /// <param name="value">The tag value.</param>
        public void SetTag(string key, string value)
        {
            ConfigureScope(scope => scope.SetTag(key, value));
        }

        /// <summary>Sets the affected user on the current pushed scope, or on the global scope.</summary>
        /// <param name="user">The user information, or <see langword="null"/> to clear it.</param>
        public void SetUser(UserInfo? user)
        {
            ConfigureScope(scope => scope.SetUser(user));
        }

        /// <summary>Sets a context namespace on the current pushed scope, or on the global scope.</summary>
        /// <param name="key">The context name, for example <c>game</c>.</param>
        /// <param name="value">The context entries.</param>
        public void SetContext(string key, IDictionary<string, object?> value)
        {
            ConfigureScope(scope => scope.SetContext(key, value));
        }

        /// <summary>Appends a breadcrumb to the current pushed scope, or to the global scope.</summary>
        /// <param name="breadcrumb">The breadcrumb to add.</param>
        public void AddBreadcrumb(Breadcrumb breadcrumb)
        {
            ConfigureScope(scope => scope.AddBreadcrumb(breadcrumb));
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

        private Task<Guid> Capture(GlitchPortEvent e, bool levelFromCaptureApi)
        {
            var eventId = e.EventId;

            try
            {
                eventId = Process(e, eventId, levelFromCaptureApi);
            }
            catch (Exception)
            {
                // Capture must never throw into the game, no matter what a
                // BeforeSend callback, a scope or the serializer does.
            }

            return Task.FromResult(eventId);
        }

        private Guid Process(GlitchPortEvent e, Guid eventId, bool levelFromCaptureApi)
        {
            MergeScopes(e, levelFromCaptureApi);
            ApplyOptions(e);
            AddDefaultContexts(e);

            if (!ShouldSample())
            {
                return eventId;
            }

            var beforeSend = _options.BeforeSend;
            if (beforeSend is not null)
            {
                GlitchPortEvent? processed;
                try
                {
                    processed = beforeSend(e);
                }
                catch (Exception)
                {
                    // A failing hook must not lose the event; keep the event as it
                    // was before the hook was called.
                    processed = e;
                }

                if (processed is null)
                {
                    return eventId;
                }

                e = processed;
            }

            if (e.EventId == Guid.Empty)
            {
                e.EventId = eventId;
            }

            if (e.Timestamp == default)
            {
                e.Timestamp = DateTimeOffset.UtcNow;
            }

            StripDefaultPii(e);

            var json = EventSerializer.Serialize(e, _sdkName, _sdkVersion);
            var captured = new CapturedEvent(
                e.EventId,
                e.Timestamp,
                e.Level,
                GetDisplayMessage(e),
                _sdkName,
                _sdkVersion,
                json,
                CreateExceptionSummaries(e),
                CopyTags(e),
                e.Release,
                e.Environment);

            _fanout.Enqueue(captured);

            return e.EventId;
        }

        private void MergeScopes(GlitchPortEvent e, bool levelFromCaptureApi)
        {
            // The global scope is always the outermost one; pushed scopes follow
            // from the outermost to the innermost.
            var scopes = new List<Scope> { _globalScope };
            var pushed = new List<Scope>();
            for (var scope = _currentScope.Value; scope is not null; scope = scope.Parent)
            {
                pushed.Add(scope);
            }

            for (var i = pushed.Count - 1; i >= 0; i--)
            {
                scopes.Add(pushed[i]);
            }

            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            var contexts = new Dictionary<string, IDictionary<string, object?>>(StringComparer.Ordinal);
            var breadcrumbs = new List<Breadcrumb>();
            IList<string>? fingerprint = null;
            UserInfo? scopeUser = null;
            Level? scopeLevel = null;

            for (var i = 0; i < scopes.Count; i++)
            {
                var scope = scopes[i];

                if (scope.Tags is not null)
                {
                    foreach (var tag in scope.Tags)
                    {
                        if (tag.Key is not null)
                        {
                            tags[tag.Key] = tag.Value ?? string.Empty;
                        }
                    }
                }

                if (scope.Contexts is not null)
                {
                    MergeContexts(contexts, scope.Contexts);
                }

                if (scope.Breadcrumbs is not null)
                {
                    foreach (var breadcrumb in scope.Breadcrumbs)
                    {
                        if (breadcrumb is not null)
                        {
                            breadcrumbs.Add(breadcrumb);
                        }
                    }
                }

                if (scope.Fingerprint is { Count: > 0 })
                {
                    fingerprint = new List<string>(scope.Fingerprint);
                }

                if (scope.User is not null)
                {
                    scopeUser = scope.User;
                }

                if (scope.Level.HasValue)
                {
                    scopeLevel = scope.Level;
                }
            }

            // The event wins over every scope, both for scalar values and for
            // individual tag and context entries.
            if (e.Tags is not null)
            {
                foreach (var tag in e.Tags)
                {
                    if (tag.Key is not null)
                    {
                        tags[tag.Key] = tag.Value ?? string.Empty;
                    }
                }
            }

            e.Tags = tags;

            if (e.Contexts is not null)
            {
                MergeContexts(contexts, e.Contexts);
            }

            e.Contexts = contexts;

            if (e.Breadcrumbs is not null)
            {
                foreach (var breadcrumb in e.Breadcrumbs)
                {
                    if (breadcrumb is not null)
                    {
                        breadcrumbs.Add(breadcrumb);
                    }
                }
            }

            e.Breadcrumbs = TrimBreadcrumbs(breadcrumbs);

            if (e.Fingerprint is not { Count: > 0 } && fingerprint is not null)
            {
                e.Fingerprint = fingerprint;
            }

            if (e.User is null)
            {
                if (scopeUser is not null)
                {
                    e.User = CloneUser(scopeUser);
                }
            }
            else
            {
                // The event owns its user object; cloning keeps a later PII
                // filtering pass from mutating caller or scope state.
                e.User = CloneUser(e.User);
            }

            // A scope level replaces the level handed to the capture method, not
            // an explicit level the caller set on a fully built event.
            if (levelFromCaptureApi && scopeLevel.HasValue)
            {
                e.Level = scopeLevel.Value;
            }
        }

        private static void MergeContexts(
            IDictionary<string, IDictionary<string, object?>> target,
            IDictionary<string, IDictionary<string, object?>> source)
        {
            foreach (var context in source)
            {
                if (context.Value is null)
                {
                    continue;
                }

                if (!target.TryGetValue(context.Key, out var entries) || entries is null)
                {
                    entries = new Dictionary<string, object?>(StringComparer.Ordinal);
                    target[context.Key] = entries;
                }

                foreach (var entry in context.Value)
                {
                    entries[entry.Key] = entry.Value;
                }
            }
        }

        private IList<Breadcrumb> TrimBreadcrumbs(List<Breadcrumb> breadcrumbs)
        {
            var max = Math.Max(0, _options.MaxBreadcrumbs);
            if (breadcrumbs.Count <= max)
            {
                return breadcrumbs;
            }

            return breadcrumbs.GetRange(breadcrumbs.Count - max, max);
        }

        private void ApplyOptions(GlitchPortEvent e)
        {
            if (string.IsNullOrEmpty(e.Release))
            {
                e.Release = _options.Release;
            }

            if (string.IsNullOrEmpty(e.Environment))
            {
                e.Environment = _options.Environment;
            }

            if (string.IsNullOrEmpty(e.ServerName))
            {
                e.ServerName = _options.ServerName;
            }

            // The event model starts at "dotnet", so a default value means the
            // caller did not choose a platform; the configured one wins then.
            if (string.IsNullOrWhiteSpace(e.Platform)
                || (string.Equals(e.Platform, GlitchPortEvent.DefaultPlatform, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(_options.Platform)))
            {
                e.Platform = string.IsNullOrWhiteSpace(_options.Platform)
                    ? GlitchPortEvent.DefaultPlatform
                    : _options.Platform;
            }
        }

        private static void AddDefaultContexts(GlitchPortEvent e)
        {
            AddDefaultContext(
                e,
                "runtime",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = RuntimeContextName,
                    ["version"] = Environment.Version.ToString(),
                    ["framework"] = RuntimeInformation.FrameworkDescription,
                });

            AddDefaultContext(
                e,
                "os",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = RuntimeInformation.OSDescription,
                    ["description"] = RuntimeInformation.OSDescription,
                });
        }

        private static void AddDefaultContext(
            GlitchPortEvent e,
            string name,
            IDictionary<string, object?> defaults)
        {
            if (e.Contexts is null)
            {
                e.Contexts = new Dictionary<string, IDictionary<string, object?>>(StringComparer.Ordinal);
            }

            if (e.Contexts.TryGetValue(name, out var existing) && existing is not null)
            {
                // User values are never overwritten: defaults only fill the gaps.
                foreach (var entry in defaults)
                {
                    if (!existing.ContainsKey(entry.Key))
                    {
                        existing[entry.Key] = entry.Value;
                    }
                }

                return;
            }

            e.Contexts[name] = defaults;
        }

        private bool ShouldSample()
        {
            var sampleRate = _options.SampleRate;
            if (sampleRate >= 1d)
            {
                return true;
            }

            if (sampleRate <= 0d)
            {
                return false;
            }

            double sample;
            lock (_samplerGate)
            {
                sample = _sampler.NextDouble();
            }

            return sample < sampleRate;
        }

        private void StripDefaultPii(GlitchPortEvent e)
        {
            if (_options.SendDefaultPii || e.User is null || e.User.IpAddress is null)
            {
                return;
            }

            // Clone before clearing so a user object handed to CaptureEventAsync or
            // assigned by BeforeSend is never mutated by the SDK.
            e.User = CloneUser(e.User);
            e.User.IpAddress = null;
        }

        private static IReadOnlyList<CapturedExceptionSummary> CreateExceptionSummaries(GlitchPortEvent e)
        {
            var exceptions = e.Exceptions;
            if (exceptions is null || exceptions.Count == 0)
            {
                return Array.Empty<CapturedExceptionSummary>();
            }

            var summaries = new List<CapturedExceptionSummary>(exceptions.Count);
            for (var i = 0; i < exceptions.Count; i++)
            {
                var exception = exceptions[i];
                if (exception is null)
                {
                    continue;
                }

                summaries.Add(new CapturedExceptionSummary(
                    exception.Type,
                    exception.Value,
                    SelectFrames(exception.Frames)));
            }

            return summaries;
        }

        private static IReadOnlyList<CapturedStackFrame> SelectFrames(IList<StackFrameInfo>? frames)
        {
            const int maxFrames = 5;
            var selected = new List<CapturedStackFrame>(maxFrames);

            if (frames is null)
            {
                return selected;
            }

            // Frames are stored from the outermost caller to the crash site, so
            // walking backwards visits the throw site first.
            for (var i = frames.Count - 1; i >= 0 && selected.Count < maxFrames; i--)
            {
                var frame = frames[i];
                if (frame is null || !frame.InApp)
                {
                    continue;
                }

                selected.Add(new CapturedStackFrame(
                    frame.Module,
                    frame.Function,
                    frame.FileName,
                    frame.LineNumber));
            }

            return selected;
        }

        private static string GetDisplayMessage(GlitchPortEvent e)
        {
            if (!string.IsNullOrEmpty(e.Message))
            {
                return e.Message!;
            }

            var exceptions = e.Exceptions;
            if (exceptions is not null && exceptions.Count > 0)
            {
                for (var i = exceptions.Count - 1; i >= 0; i--)
                {
                    var exception = exceptions[i];
                    if (exception is null)
                    {
                        continue;
                    }

                    var type = string.IsNullOrEmpty(exception.Type) ? "Exception" : exception.Type;
                    return string.IsNullOrEmpty(exception.Value)
                        ? type
                        : type + ": " + exception.Value;
                }
            }

            return string.Empty;
        }

        private static IReadOnlyDictionary<string, string> CopyTags(GlitchPortEvent e)
        {
            return e.Tags is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(e.Tags, StringComparer.Ordinal);
        }

        private static UserInfo CloneUser(UserInfo user)
        {
            return new UserInfo
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                IpAddress = user.IpAddress,
            };
        }

        private static string DescribeException(Exception exception)
        {
            var type = exception.GetType().FullName ?? exception.GetType().Name;

            try
            {
                // Message is virtual: a user exception can override it and throw,
                // and capture must still never throw to the game.
                return string.IsNullOrEmpty(exception.Message) ? type : type + ": " + exception.Message;
            }
            catch (Exception)
            {
                return type;
            }
        }

        private Task<Guid>? GetCaptureFailure(CancellationToken ct)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return Task.FromException<Guid>(new ObjectDisposedException(nameof(GlitchPortClient)));
            }

            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled<Guid>(ct);
            }

            return null;
        }

        private string ResolveInitialPlatform()
        {
            return string.IsNullOrWhiteSpace(_options.Platform)
                ? GlitchPortEvent.DefaultPlatform
                : _options.Platform;
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
                _unhandledExceptionIntegration?.Dispose();
            }
            catch (Exception)
            {
                // Unsubscribing from process-wide events is best effort.
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

        private sealed class PushedScope : IDisposable
        {
            private readonly GlitchPortClient _client;
            private readonly Scope _scope;
            private readonly Scope? _parent;

            private int _disposed;

            public PushedScope(GlitchPortClient client, Scope scope, Scope? parent)
            {
                _client = client;
                _scope = scope;
                _parent = parent;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                // Only pop our own scope; an out-of-order dispose must not discard
                // a newer scope pushed by the same flow.
                if (ReferenceEquals(_client._currentScope.Value, _scope))
                {
                    _client._currentScope.Value = _parent;
                }
            }
        }
    }
}
