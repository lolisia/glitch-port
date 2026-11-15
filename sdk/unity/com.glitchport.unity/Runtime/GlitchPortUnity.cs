#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GlitchPort.Unity
{
    /// <summary>
    /// The public entry point of the Unity SDK. It owns the process-wide
    /// <see cref="GlitchPortClient"/>, initializes it from
    /// <see cref="GlitchPortSettings"/>, and forwards Unity log messages to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capture methods only enqueue events and return immediately; delivery
    /// failures are reported by <see cref="FlushAsync"/> instead of being thrown
    /// at the capture site. Every public member is safe to call before
    /// initialization and again after <see cref="Close"/>.
    /// </para>
    /// <para>
    /// The Core scope is not thread safe. Every scope mutation, breadcrumb and
    /// capture is therefore serialized through one private lock so the threaded
    /// Unity log callback, the lifecycle breadcrumb tracker and game code can
    /// never interleave scope writes.
    /// </para>
    /// </remarks>
    public static class GlitchPortUnity
    {
        /// <summary>The Unity SDK package version reported to glitch-port.</summary>
        public const string PackageVersion = "0.1.0";

        /// <summary>The SDK name reported to glitch-port.</summary>
        private const string SdkName = "glitchport.unity";

        /// <summary>The platform name reported on every event.</summary>
        private const string UnityPlatformName = "unity";

        /// <summary>The prefix used by every diagnostic this SDK writes to the Unity console.</summary>
        private const string DiagnosticPrefix = "[GlitchPort]";

        private const string SettingsResourceName = "GlitchPortSettings";
        private const string DsnEnvironmentVariable = "GLITCHPORT_DSN";
        private const string TerminalStreamingAssetsFolder = "GlitchPort";
        private const string TerminalExecutableName = "glitchport-terminal.exe";

        // The BuildOptions diagnostic that is worth emitting only once per play
        // session: repeating it on every failed Init would flood the console.
        private const string NoRelayDiagnosticPrefix = "No glitch-port relay is configured";

        /// <summary>
        /// Serializes every read and write of the Core scope: captures, tags,
        /// contexts, user and breadcrumbs.
        /// </summary>
        private static readonly object CaptureGate = new object();

        private static GlitchPortClient? _client;
        private static GlitchPortSettings? _settings;
        private static UnityContextProvider? _contextProvider;
        private static UnityBreadcrumbTracker? _breadcrumbTracker;
        private static int _mainThreadId;
        private static bool _unsupportedPlatformWarned;
        private static bool _noRelayDiagnosticLogged;

        /// <summary>Gets a value indicating whether the SDK has an active client.</summary>
        public static bool IsInitialized => Volatile.Read(ref _client) is not null;

        /// <summary>
        /// Gets the active client, or <see langword="null"/> before initialization
        /// and after <see cref="Close"/>.
        /// </summary>
        public static GlitchPortClient? Client => Volatile.Read(ref _client);

        /// <summary>
        /// Initializes the SDK from <c>Resources/GlitchPortSettings</c>. When the
        /// asset does not exist, the settings defaults are used.
        /// </summary>
        /// <returns><see langword="true"/> when a client is active after the call.</returns>
        public static bool Init()
        {
            if (IsInitialized)
            {
                return true;
            }

            try
            {
                var settings = LoadSettings();
                if (!settings.Enabled)
                {
                    return false;
                }

                var platform = CreatePlatformInfo();
                if (!GlitchPortUnityConfig.IsSupported(platform))
                {
                    WarnUnsupportedPlatformOnce();
                    return false;
                }

                var diagnostics = new List<string>();
                var environmentDsn = Environment.GetEnvironmentVariable(DsnEnvironmentVariable);
                var options = GlitchPortUnityConfig.BuildOptions(
                    settings,
                    platform,
                    environmentDsn,
                    diagnostics);

                LogDiagnostics(settings.DebugLogging, diagnostics);

                if (options is null)
                {
                    return false;
                }

                options.Platform = UnityPlatformName;

                return TryInitialize(settings, options);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(DiagnosticPrefix + " Initialization failed: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Initializes the SDK with caller-supplied options. This is the explicit
        /// opt-in used when automatic initialization is disabled: it ignores
        /// <see cref="GlitchPortSettings.Enabled"/> and never replaces an already
        /// active client.
        /// </summary>
        /// <param name="options">The Core client options.</param>
        /// <returns><see langword="true"/> when a client is active after the call.</returns>
        public static bool Init(GlitchPortOptions options)
        {
            if (options is null)
            {
                return false;
            }

            if (IsInitialized)
            {
                return true;
            }

            try
            {
                var platform = CreatePlatformInfo();
                if (!GlitchPortUnityConfig.IsSupported(platform))
                {
                    WarnUnsupportedPlatformOnce();
                    return false;
                }

                // The explicit path still needs the settings for the log bridge,
                // even though the Enabled flag does not gate initialization.
                var settings = LoadSettings();

                options.SdkName = SdkName;
                options.SdkVersion = PackageVersion;
                options.Platform = UnityPlatformName;

                return TryInitialize(settings, options);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(DiagnosticPrefix + " Initialization failed: " + ex);
                return false;
            }
        }

        /// <summary>Sets a tag on the active client's scope.</summary>
        /// <param name="key">The tag name.</param>
        /// <param name="value">The tag value.</param>
        /// <remarks>
        /// The call is ignored when the SDK is not initialized or the arguments
        /// are invalid. It never throws into the game.
        /// </remarks>
        public static void SetTag(string key, string value)
        {
            if (key is null)
            {
                return;
            }

            MutateScope(client => client.SetTag(key, value));
        }

        /// <summary>Sets the affected user on the active client's scope.</summary>
        /// <param name="user">The user information.</param>
        /// <remarks>
        /// The call is ignored when the SDK is not initialized or
        /// <paramref name="user"/> is <see langword="null"/>. It never throws
        /// into the game.
        /// </remarks>
        public static void SetUser(UserInfo? user)
        {
            if (user is null)
            {
                return;
            }

            MutateScope(client => client.SetUser(user));
        }

        /// <summary>Sets a context namespace on the active client's scope.</summary>
        /// <param name="key">The context name, for example <c>build</c>.</param>
        /// <param name="value">The context entries.</param>
        /// <remarks>
        /// The call is ignored when the SDK is not initialized or an argument is
        /// <see langword="null"/>. It never throws into the game.
        /// </remarks>
        public static void SetContext(string key, IDictionary<string, object?> value)
        {
            if (key is null || value is null)
            {
                return;
            }

            MutateScope(client => client.SetContext(key, value));
        }

        /// <summary>Adds a breadcrumb to the active client's scope.</summary>
        /// <param name="breadcrumb">The breadcrumb to record.</param>
        /// <remarks>
        /// The call is ignored when the SDK is not initialized or
        /// <paramref name="breadcrumb"/> is <see langword="null"/>. It never
        /// throws into the game.
        /// </remarks>
        public static void AddBreadcrumb(Breadcrumb breadcrumb)
        {
            AddBreadcrumbInternal(breadcrumb);
        }

        /// <summary>
        /// Enqueues a message event and returns its id immediately. The call never
        /// waits for delivery.
        /// </summary>
        /// <param name="message">The message text.</param>
        /// <param name="level">The event severity.</param>
        /// <returns>
        /// The event id, or <see langword="null"/> when the SDK is not initialized
        /// or <paramref name="message"/> is <see langword="null"/>.
        /// </returns>
        public static Guid? CaptureMessage(string message, Level level = Level.Info)
        {
            if (message is null)
            {
                return null;
            }

            return Capture(client => client.CaptureMessageAsync(message, level).GetAwaiter().GetResult());
        }

        /// <summary>
        /// Enqueues an exception as a structured event and returns its id
        /// immediately. The call never waits for delivery.
        /// </summary>
        /// <param name="exception">The exception to report.</param>
        /// <param name="level">The event severity.</param>
        /// <returns>
        /// The event id, or <see langword="null"/> when the SDK is not initialized
        /// or <paramref name="exception"/> is <see langword="null"/>.
        /// </returns>
        public static Guid? CaptureException(Exception exception, Level level = Level.Error)
        {
            if (exception is null)
            {
                return null;
            }

            return Capture(client =>
                client.CaptureExceptionAsync(exception, level).GetAwaiter().GetResult());
        }

        /// <summary>
        /// Flushes every relay. The method never throws; failures are represented
        /// by the returned report.
        /// </summary>
        /// <param name="timeout">
        /// The flush timeout per relay, or <see langword="null"/> to use the Core
        /// default of five seconds.
        /// </param>
        /// <returns>
        /// The delivery report, or <see langword="null"/> when the SDK is not
        /// initialized or the flush could not be performed.
        /// </returns>
        public static async Task<DeliveryReport?> FlushAsync(TimeSpan? timeout = null)
        {
            var client = Volatile.Read(ref _client);
            if (client is null)
            {
                return null;
            }

            try
            {
                return await client.FlushAsync(timeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Stops the lifecycle tracker, unsubscribes from Unity callbacks, clears
        /// the context provider and disposes the active client after a bounded
        /// flush. The method is idempotent, never throws, and allows a later
        /// <see cref="Init()"/> call.
        /// </summary>
        public static void Close()
        {
            UnityBreadcrumbTracker? tracker;
            GlitchPortClient? client;

            lock (CaptureGate)
            {
                client = _client;
                tracker = _breadcrumbTracker;
                _client = null;
                _breadcrumbTracker = null;
                _settings = null;
            }

            try
            {
                tracker?.Dispose();
            }
            catch (Exception)
            {
                // Unity is shutting down; the tracker no longer matters.
            }

            try
            {
                Application.logMessageReceivedThreaded -= OnLogMessageReceivedThreaded;
            }
            catch (Exception)
            {
                // Unity is shutting down; the subscription no longer matters.
            }

            try
            {
                Application.quitting -= OnQuitting;
            }
            catch (Exception)
            {
                // Unity is shutting down; the subscription no longer matters.
            }

            lock (CaptureGate)
            {
                _contextProvider = null;
            }

            if (client is null)
            {
                return;
            }

            try
            {
                client.Dispose();
            }
            catch (Exception)
            {
                // Close must never throw, even while the editor is quitting.
            }
        }

        /// <summary>
        /// Disposes any client left over from a previous play session when Unity
        /// runs with domain reload disabled.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsBeforePlay()
        {
            Close();

            // Runtime initialization runs on the Unity main thread; remembering
            // it here lets log callbacks on other threads skip engine access.
            Volatile.Write(ref _mainThreadId, Thread.CurrentThread.ManagedThreadId);
            _unsupportedPlatformWarned = false;
            _noRelayDiagnosticLogged = false;
        }

        /// <summary>
        /// Initializes the SDK before the first scene loads when the project
        /// settings allow it.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitializeOnBeforeSceneLoad()
        {
            try
            {
                var settings = LoadSettings();
                if (!settings.Enabled || !settings.AutoInitialize)
                {
                    return;
                }

                var platform = CreatePlatformInfo();
                if (!GlitchPortUnityConfig.IsSupported(platform))
                {
                    WarnUnsupportedPlatformOnce();
                    return;
                }

                Init();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(DiagnosticPrefix + " Automatic initialization failed: " + ex);
            }
        }

        private static GlitchPortSettings LoadSettings()
        {
            var settings = Resources.Load<GlitchPortSettings>(SettingsResourceName);
            return settings is not null
                ? settings
                : ScriptableObject.CreateInstance<GlitchPortSettings>();
        }

        private static UnityPlatformInfo CreatePlatformInfo()
        {
            var platform = Application.platform;
            var isEditor = Application.isEditor;
            var isDevelopmentBuild = Debug.isDebugBuild;

            return new UnityPlatformInfo(
                isEditor,
                isDevelopmentBuild,
                platform == RuntimePlatform.WindowsEditor || platform == RuntimePlatform.WindowsPlayer,
                CanStartProcess(platform),
                platform != RuntimePlatform.WebGLPlayer,
                Application.persistentDataPath,
                Application.productName,
                PackageVersion,
                ResolveTerminalExecutablePath(isEditor, isDevelopmentBuild),
                Application.version);
        }

        private static bool CanStartProcess(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                    return true;
                default:
                    return false;
            }
        }

        private static string? ResolveTerminalExecutablePath(bool isEditor, bool isDevelopmentBuild)
        {
            if (isEditor)
            {
                return UnityPlatformInfo.TerminalExecutablePathOverride;
            }

            if (!isDevelopmentBuild)
            {
                return null;
            }

            try
            {
                var path = Path.Combine(
                    Application.streamingAssetsPath,
                    TerminalStreamingAssetsFolder,
                    TerminalExecutableName);

                return File.Exists(path) ? path : null;
            }
            catch (Exception)
            {
                // A platform whose streaming assets path cannot be inspected
                // simply does not get the terminal relay.
                return null;
            }
        }

        private static bool TryInitialize(GlitchPortSettings settings, GlitchPortOptions options)
        {
            RememberMainThread();

            GlitchPortClient? client = null;
            UnityContextProvider? provider = null;
            UnityBreadcrumbTracker? tracker = null;

            try
            {
                client = new GlitchPortClient(options);

                provider = new UnityContextProvider(new UnitySystemContextSource());

                // Collect reads engine state, so it must run on the main thread.
                // Init is a main-thread API; a caller that breaks that contract
                // still gets a working client with the default snapshot.
                if (IsMainThread())
                {
                    provider.Collect();
                }

                lock (CaptureGate)
                {
                    ApplyContexts(client, provider);

                    if (options.SendDefaultPii)
                    {
                        var user = provider.CreateUser(sendDefaultPii: true);
                        if (user is not null)
                        {
                            client.SetUser(user);
                        }
                    }

                    // Publish the state before subscribing so a log emitted
                    // between the two can already be captured.
                    _settings = settings;
                    _client = client;
                    _contextProvider = provider;
                }

                tracker = new UnityBreadcrumbTracker(AddBreadcrumbInternal);
                tracker.Start();

                lock (CaptureGate)
                {
                    _breadcrumbTracker = tracker;
                }

                Application.logMessageReceivedThreaded += OnLogMessageReceivedThreaded;
                Application.quitting += OnQuitting;
                return true;
            }
            catch (Exception ex)
            {
                lock (CaptureGate)
                {
                    _client = null;
                    _settings = null;
                    _contextProvider = null;
                    _breadcrumbTracker = null;
                }

                try
                {
                    tracker?.Dispose();
                }
                catch (Exception)
                {
                }

                try
                {
                    Application.logMessageReceivedThreaded -= OnLogMessageReceivedThreaded;
                }
                catch (Exception)
                {
                }

                try
                {
                    Application.quitting -= OnQuitting;
                }
                catch (Exception)
                {
                }

                try
                {
                    client?.Dispose();
                }
                catch (Exception)
                {
                }

                Debug.LogWarning(DiagnosticPrefix + " Client creation failed: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Enqueues an event through the active client after refreshing and
        /// re-applying the Unity context. The caller must not hold
        /// <see cref="CaptureGate"/>.
        /// </summary>
        private static Guid? Capture(Func<GlitchPortClient, Guid> capture)
        {
            try
            {
                lock (CaptureGate)
                {
                    var client = _client;
                    if (client is null)
                    {
                        return null;
                    }

                    RefreshContexts(client);
                    return capture(client);
                }
            }
            catch (Exception)
            {
                // Capture must never throw into the game.
                return null;
            }
        }

        /// <summary>
        /// Applies a scope mutation through the active client. The caller must
        /// not hold <see cref="CaptureGate"/>.
        /// </summary>
        private static void MutateScope(Action<GlitchPortClient> mutate)
        {
            try
            {
                lock (CaptureGate)
                {
                    var client = _client;
                    if (client is null)
                    {
                        return;
                    }

                    mutate(client);
                }
            }
            catch (Exception)
            {
                // Scope changes must never throw into the game.
            }
        }

        /// <summary>
        /// Refreshes the per-frame game context on the main thread and re-applies
        /// every context namespace and tag. The caller must hold
        /// <see cref="CaptureGate"/>.
        /// </summary>
        private static void RefreshContexts(GlitchPortClient client)
        {
            var provider = _contextProvider;
            if (provider is null)
            {
                return;
            }

            if (IsMainThread())
            {
                try
                {
                    provider.RefreshFrame();
                }
                catch (Exception)
                {
                    // The cached frame snapshot remains usable.
                }
            }

            ApplyContexts(client, provider);
        }

        /// <summary>
        /// Applies every context namespace and tag from <paramref name="provider"/>.
        /// The caller must hold <see cref="CaptureGate"/>.
        /// </summary>
        private static void ApplyContexts(GlitchPortClient client, UnityContextProvider provider)
        {
            foreach (var context in provider.CreateContexts())
            {
                client.SetContext(context.Key, context.Value);
            }

            foreach (var tag in provider.CreateTags())
            {
                client.SetTag(tag.Key, tag.Value);
            }
        }

        /// <summary>
        /// The tracker callback: adds a lifecycle breadcrumb through the same lock
        /// as captures so it cannot corrupt the Core scope.
        /// </summary>
        private static void AddBreadcrumbInternal(Breadcrumb breadcrumb)
        {
            if (breadcrumb is null)
            {
                return;
            }

            MutateScope(client => client.AddBreadcrumb(breadcrumb));
        }

        private static void OnLogMessageReceivedThreaded(string condition, string stackTrace, LogType type)
        {
            try
            {
                var settings = Volatile.Read(ref _settings);
                if (settings is null)
                {
                    return;
                }

                var mapping = UnityLogBridge.Map(condition, stackTrace, type, settings);
                if (mapping.IsEmpty)
                {
                    return;
                }

                try
                {
                    lock (CaptureGate)
                    {
                        var client = _client;
                        if (client is null)
                        {
                            return;
                        }

                        if (mapping.Breadcrumb is not null)
                        {
                            // The scope breadcrumb is merged into the event below,
                            // and survives for every later capture when the event
                            // capture flag is off.
                            client.AddBreadcrumb(mapping.Breadcrumb);
                        }

                        if (mapping.Event is not null)
                        {
                            RefreshContexts(client);
                            client.CaptureEventAsync(mapping.Event).GetAwaiter().GetResult();
                        }
                    }
                }
                catch (Exception)
                {
                    // A log callback must never throw into Unity's logging pipeline.
                }
            }
            catch (Exception)
            {
                // A log callback must never throw into Unity's logging pipeline.
            }
        }

        private static void OnQuitting()
        {
            Close();
        }

        private static bool IsMainThread()
        {
            var mainThreadId = Volatile.Read(ref _mainThreadId);
            return mainThreadId == 0 || mainThreadId == Thread.CurrentThread.ManagedThreadId;
        }

        private static void RememberMainThread()
        {
            if (Volatile.Read(ref _mainThreadId) == 0)
            {
                Interlocked.CompareExchange(
                    ref _mainThreadId,
                    Thread.CurrentThread.ManagedThreadId,
                    0);
            }
        }

        private static void WarnUnsupportedPlatformOnce()
        {
            if (_unsupportedPlatformWarned)
            {
                return;
            }

            _unsupportedPlatformWarned = true;
            Debug.LogWarning(
                DiagnosticPrefix
                + " glitch-port is not supported on "
                + Application.platform
                + "; initialization was skipped.");
        }

        private static void LogDiagnostics(bool debugLogging, List<string> diagnostics)
        {
            if (!debugLogging)
            {
                return;
            }

            for (var i = 0; i < diagnostics.Count; i++)
            {
                var diagnostic = diagnostics[i];

                if (diagnostic.StartsWith(NoRelayDiagnosticPrefix, StringComparison.Ordinal))
                {
                    if (_noRelayDiagnosticLogged)
                    {
                        continue;
                    }

                    _noRelayDiagnosticLogged = true;
                }

                Debug.LogWarning(DiagnosticPrefix + " " + diagnostic);
            }
        }
    }
}
