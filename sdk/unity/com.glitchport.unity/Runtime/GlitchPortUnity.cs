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
    /// Capture methods only enqueue events and return immediately; delivery
    /// failures are reported by <see cref="FlushAsync"/> instead of being thrown
    /// at the capture site. Every public member is safe to call before
    /// initialization and again after <see cref="Close"/>.
    /// </remarks>
    public static class GlitchPortUnity
    {
        /// <summary>The Unity SDK package version reported to glitch-port.</summary>
        public const string PackageVersion = "0.1.0";

        /// <summary>The SDK name reported to glitch-port.</summary>
        private const string SdkName = "glitchport.unity";

        /// <summary>The prefix used by every diagnostic this SDK writes to the Unity console.</summary>
        private const string DiagnosticPrefix = "[GlitchPort]";

        private const string SettingsResourceName = "GlitchPortSettings";
        private const string DsnEnvironmentVariable = "GLITCHPORT_DSN";
        private const string TerminalStreamingAssetsFolder = "GlitchPort";
        private const string TerminalExecutableName = "glitchport-terminal.exe";

        // The BuildOptions diagnostic that is worth emitting only once per play
        // session: repeating it on every failed Init would flood the console.
        private const string NoRelayDiagnosticPrefix = "No glitch-port relay is configured";

        private static GlitchPortClient? _client;
        private static GlitchPortSettings? _settings;
        private static bool _unsupportedPlatformWarned;
        private static bool _noRelayDiagnosticLogged;

        /// <summary>Gets a value indicating whether the SDK has an active client.</summary>
        public static bool IsInitialized => _client is not null;

        /// <summary>
        /// Gets the active client, or <see langword="null"/> before initialization
        /// and after <see cref="Close"/>.
        /// </summary>
        public static GlitchPortClient? Client => _client;

        /// <summary>
        /// Initializes the SDK from <c>Resources/GlitchPortSettings</c>. When the
        /// asset does not exist, the settings defaults are used.
        /// </summary>
        /// <returns><see langword="true"/> when a client is active after the call.</returns>
        public static bool Init()
        {
            if (_client is not null)
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

            if (_client is not null)
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

                return TryInitialize(settings, options);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(DiagnosticPrefix + " Initialization failed: " + ex);
                return false;
            }
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

            var client = _client;
            if (client is null)
            {
                return null;
            }

            try
            {
                return client.CaptureMessageAsync(message, level).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Capture must never throw into the game.
                return null;
            }
        }

        /// <summary>
        /// Enqueues an exception as an event whose message is
        /// <c>Type: Message</c> followed by the stack trace when one is available.
        /// The call never waits for delivery.
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

            try
            {
                var exceptionType = exception.GetType();
                var message = (exceptionType.FullName ?? exceptionType.Name) + ": " + exception.Message;
                var stackTrace = exception.StackTrace;
                if (!string.IsNullOrEmpty(stackTrace))
                {
                    message += "\n" + stackTrace;
                }

                return CaptureMessage(message, level);
            }
            catch (Exception)
            {
                return null;
            }
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
            var client = _client;
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
        /// Unsubscribes from Unity callbacks and disposes the active client after a
        /// bounded flush. The method is idempotent, never throws, and allows a
        /// later <see cref="Init()"/> call.
        /// </summary>
        public static void Close()
        {
            var client = Interlocked.Exchange(ref _client, null);
            _settings = null;

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
                ResolveTerminalExecutablePath(isEditor, isDevelopmentBuild));
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
            GlitchPortClient? client = null;

            try
            {
                client = new GlitchPortClient(options);

                // Publish the state before subscribing so a log emitted between
                // the two can already be captured.
                _settings = settings;
                _client = client;

                Application.logMessageReceivedThreaded += OnLogMessageReceivedThreaded;
                Application.quitting += OnQuitting;
                return true;
            }
            catch (Exception ex)
            {
                _client = null;
                _settings = null;

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

        private static void OnLogMessageReceivedThreaded(string condition, string stackTrace, LogType type)
        {
            try
            {
                var settings = _settings;
                if (settings is null)
                {
                    return;
                }

                if (!UnityLogBridge.TryMap(condition, stackTrace, type, settings, out var message, out var level))
                {
                    return;
                }

                CaptureMessage(message, level);
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
