#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace GlitchPort.Unity
{
    /// <summary>
    /// The platform facts the Unity SDK uses to decide which relays it can
    /// create. The runtime fills this from <c>UnityEngine.Application</c>; tests
    /// construct it directly so every rule is deterministic.
    /// </summary>
    public sealed class UnityPlatformInfo
    {
        /// <summary>The product version used when the caller does not report one.</summary>
        public const string DefaultProductVersion = "0.0.0";

        /// <summary>Creates platform information.</summary>
        /// <param name="isEditor">Whether the game is running in the Unity editor.</param>
        /// <param name="isDevelopmentBuild">Whether this is a development player.</param>
        /// <param name="isWindows">Whether the current platform is Windows.</param>
        /// <param name="canStartProcess">Whether the platform can start a child process.</param>
        /// <param name="supportsHttp">Whether the platform can use HTTP, which is false for WebGL.</param>
        /// <param name="persistentDataPath">The platform's persistent data directory.</param>
        /// <param name="productName">The product name shown by the terminal renderer.</param>
        /// <param name="packageVersion">The Unity SDK package version.</param>
        /// <param name="terminalExecutablePath">The terminal executable path, or <see langword="null"/> when it was not found.</param>
        /// <param name="productVersion">
        /// The application version reported in the default release, or
        /// <see langword="null"/> when the caller does not know it.
        /// </param>
        public UnityPlatformInfo(
            bool isEditor,
            bool isDevelopmentBuild,
            bool isWindows,
            bool canStartProcess,
            bool supportsHttp,
            string persistentDataPath,
            string productName,
            string packageVersion,
            string? terminalExecutablePath,
            string? productVersion = null)
        {
            IsEditor = isEditor;
            IsDevelopmentBuild = isDevelopmentBuild;
            IsWindows = isWindows;
            CanStartProcess = canStartProcess;
            SupportsHttp = supportsHttp;
            PersistentDataPath = persistentDataPath ?? string.Empty;
            ProductName = productName ?? string.Empty;
            PackageVersion = packageVersion ?? string.Empty;
            TerminalExecutablePath = terminalExecutablePath;
            ProductVersion = string.IsNullOrWhiteSpace(productVersion) ? DefaultProductVersion : productVersion;
        }

        /// <summary>Gets a value indicating whether the game is running in the Unity editor.</summary>
        public bool IsEditor { get; }

        /// <summary>Gets a value indicating whether this is a development player.</summary>
        public bool IsDevelopmentBuild { get; }

        /// <summary>Gets a value indicating whether the current platform is Windows.</summary>
        public bool IsWindows { get; }

        /// <summary>Gets a value indicating whether the platform can start a child process.</summary>
        public bool CanStartProcess { get; }

        /// <summary>Gets a value indicating whether the platform supports HTTP delivery.</summary>
        public bool SupportsHttp { get; }

        /// <summary>Gets the platform's persistent data directory.</summary>
        public string PersistentDataPath { get; }

        /// <summary>Gets the product name passed to the terminal renderer.</summary>
        public string ProductName { get; }

        /// <summary>Gets the Unity SDK package version.</summary>
        public string PackageVersion { get; }

        /// <summary>Gets the application version reported in the default release.</summary>
        public string ProductVersion { get; }

        /// <summary>Gets the resolved terminal executable path, or <see langword="null"/> when it was not found.</summary>
        public string? TerminalExecutablePath { get; }

        /// <summary>
        /// Gets or sets the terminal executable path resolved by the editor tool
        /// locator. The runtime copies this value into its platform information;
        /// the override is never read by <see cref="GlitchPortUnityConfig.BuildOptions"/>.
        /// </summary>
        public static string? TerminalExecutablePathOverride { get; set; }
    }

    /// <summary>
    /// Converts <see cref="GlitchPortSettings"/> plus platform facts into
    /// <see cref="GlitchPortOptions"/>. This type is pure logic: it never reads
    /// the process environment, touches Unity objects, or creates relays.
    /// </summary>
    public static class GlitchPortUnityConfig
    {
        private const string SdkName = "glitchport.unity";

        private const string UnityPlatformName = "unity";

        private const string EditorEnvironment = "editor";

        private const string DevelopmentEnvironment = "development";

        private const string ProductionEnvironment = "production";

        private const string TerminalUnavailableDiagnostic =
            "The terminal relay is enabled but was not added. Run scripts/build-unity-plugins.sh to build "
            + "glitchport-terminal, and make sure the game runs on Windows with permission to start processes.";

        private const string NoRelayDiagnostic =
            "No glitch-port relay is configured. Set a DSN in GlitchPortSettings, enable the file relay, "
            + "or enable the terminal relay.";

        /// <summary>
        /// Returns whether the Unity SDK can report to a remote relay on
        /// <paramref name="platform"/>. WebGL and other platforms without HTTP
        /// support return <see langword="false"/>.
        /// </summary>
        /// <param name="platform">The platform facts.</param>
        /// <returns><see langword="true"/> when HTTP delivery is supported.</returns>
        public static bool IsSupported(UnityPlatformInfo platform)
        {
            if (platform is null)
            {
                throw new ArgumentNullException(nameof(platform));
            }

            return platform.SupportsHttp;
        }

        /// <summary>
        /// Builds the Core options for the given settings and platform.
        /// </summary>
        /// <remarks>
        /// A non-blank <paramref name="environmentDsn"/> replaces every asset
        /// relay setting and is parsed with <see cref="RelayConfigParser"/>; the
        /// method never reads the process environment itself. Without an
        /// environment DSN, the asset DSN, file mode and terminal mode rules add
        /// the relay definitions described by <see cref="GlitchPortSettings"/>,
        /// and the HTTP relay's offline queue follows
        /// <see cref="GlitchPortSettings.OfflineQueue"/> and
        /// <see cref="GlitchPortSettings.OfflineQueueMaxEvents"/>.
        /// The platform, release, environment, PII, breadcrumb and sampling
        /// options are always derived from the settings and the platform facts.
        /// </remarks>
        /// <param name="settings">The project settings.</param>
        /// <param name="platform">The platform facts.</param>
        /// <param name="environmentDsn">
        /// The relay configuration string to use instead of the asset settings,
        /// normally the <c>GLITCHPORT_DSN</c> variable supplied by the caller.
        /// </param>
        /// <param name="diagnostics">
        /// An optional collection that receives human-readable diagnostics for
        /// relays that were skipped.
        /// </param>
        /// <returns>
        /// The built options, or <see langword="null"/> when no relay was added.
        /// </returns>
        /// <exception cref="FormatException">
        /// The asset DSN or the environment relay configuration is invalid.
        /// </exception>
        public static GlitchPortOptions? BuildOptions(
            GlitchPortSettings settings,
            UnityPlatformInfo platform,
            string? environmentDsn,
            ICollection<string>? diagnostics = null)
        {
            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (platform is null)
            {
                throw new ArgumentNullException(nameof(platform));
            }

            var options = new GlitchPortOptions
            {
                SdkName = SdkName,
                SdkVersion = platform.PackageVersion,
                Platform = UnityPlatformName,
                Release = string.IsNullOrWhiteSpace(settings.Release)
                    ? platform.ProductName + "@" + platform.ProductVersion
                    : settings.Release,
                Environment = string.IsNullOrWhiteSpace(settings.Environment)
                    ? DefaultEnvironment(platform)
                    : settings.Environment,
                SendDefaultPii = settings.SendDefaultPii,
                MaxBreadcrumbs = settings.MaxBreadcrumbs,
                SampleRate = Math.Max(0d, Math.Min(1d, settings.SampleRate)),
            };

            if (!string.IsNullOrWhiteSpace(environmentDsn))
            {
                var definitions = RelayConfigParser.Parse(environmentDsn);
                for (var i = 0; i < definitions.Count; i++)
                {
                    options.Relays.Add(definitions[i]);
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(settings.Dsn) && platform.SupportsHttp)
                {
                    // The queue is part of the HTTP relay built from the asset
                    // DSN only. An environment DSN brings its own offline_*
                    // parameters through RelayConfigParser, so this branch is
                    // never reached for it.
                    var queueDirectory = settings.OfflineQueue
                        ? Path.Combine(platform.PersistentDataPath, "glitchport", "queue")
                        : null;
                    var queueMaxEvents = Math.Max(1, settings.OfflineQueueMaxEvents);

                    options.Relays.Add(Relay.Http(
                        settings.Dsn,
                        relay =>
                        {
                            relay.MinLevel = settings.HttpMinLevel;

                            // A null directory keeps the queue disabled.
                            if (queueDirectory != null)
                            {
                                relay.OfflineQueue.Directory = queueDirectory;
                                relay.OfflineQueue.MaxEvents = queueMaxEvents;
                            }
                        }));
                }

                if (IsFileEnabled(settings.FileMode, platform))
                {
                    var directory = string.IsNullOrWhiteSpace(settings.FileDirectory)
                        ? Path.Combine(platform.PersistentDataPath, "glitchport", "logs")
                        : settings.FileDirectory;

                    options.Relays.Add(Relay.File(relay =>
                    {
                        relay.MinLevel = settings.FileMinLevel;
                        relay.Directory = directory;
                    }));
                }

                if (IsTerminalEnabled(settings.TerminalMode, platform))
                {
                    var executablePath = platform.TerminalExecutablePath;
                    if (platform.CanStartProcess
                        && platform.IsWindows
                        && !string.IsNullOrWhiteSpace(executablePath))
                    {
                        options.Relays.Add(Relay.Terminal(relay =>
                        {
                            relay.Window = settings.TerminalWindow;
                            relay.AppName = platform.ProductName;
                            relay.ExecutablePath = executablePath;
                        }));
                    }
                    else
                    {
                        AddDiagnostic(diagnostics, TerminalUnavailableDiagnostic);
                    }
                }
            }

            if (options.Relays.Count == 0)
            {
                AddDiagnostic(diagnostics, NoRelayDiagnostic);
                return null;
            }

            return options;
        }

        private static string DefaultEnvironment(UnityPlatformInfo platform)
        {
            if (platform.IsEditor)
            {
                return EditorEnvironment;
            }

            return platform.IsDevelopmentBuild ? DevelopmentEnvironment : ProductionEnvironment;
        }

        private static bool IsFileEnabled(GlitchPort.Unity.FileMode mode, UnityPlatformInfo platform)
        {
            switch (mode)
            {
                case GlitchPort.Unity.FileMode.Auto:
                    return platform.IsEditor || platform.IsDevelopmentBuild;
                case GlitchPort.Unity.FileMode.On:
                    return true;
                case GlitchPort.Unity.FileMode.Off:
                    return false;
                default:
                    return false;
            }
        }

        private static bool IsTerminalEnabled(TerminalMode mode, UnityPlatformInfo platform)
        {
            switch (mode)
            {
                case TerminalMode.Auto:
                    // The terminal is a Windows-only relay, so Auto only turns it
                    // on for the two environments where a developer is watching
                    // the console.
                    return platform.IsWindows && (platform.IsEditor || platform.IsDevelopmentBuild);
                case TerminalMode.On:
                    return true;
                case TerminalMode.Off:
                    return false;
                default:
                    return false;
            }
        }

        private static void AddDiagnostic(ICollection<string>? diagnostics, string message)
        {
            diagnostics?.Add(message);
        }
    }
}
