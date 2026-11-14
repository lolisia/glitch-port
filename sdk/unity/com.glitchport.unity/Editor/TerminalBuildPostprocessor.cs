#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GlitchPort.Unity.Editor
{
    /// <summary>
    /// Copies <c>glitchport-terminal.exe</c> into the StreamingAssets folder of
    /// Windows development builds, which is where
    /// <see cref="GlitchPortUnityConfig"/> looks for it at runtime.
    /// </summary>
    /// <remarks>
    /// Release builds never receive the binary, and a copy failure only logs a
    /// warning so it can never break a player build.
    /// </remarks>
    public sealed class TerminalBuildPostprocessor : IPostprocessBuildWithReport
    {
        private const string DiagnosticPrefix = "[GlitchPort]";
        private const string ExecutableName = "glitchport-terminal.exe";

        /// <inheritdoc/>
        public int callbackOrder => 0;

        /// <inheritdoc/>
        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null)
            {
                return;
            }

            try
            {
                var summary = report.summary;
                if (!ShouldCopyTerminal(GetConfiguredTerminalMode(), summary.platform, summary.options))
                {
                    return;
                }

                CopyTerminal(summary.outputPath);
            }
            catch (Exception exception)
            {
                // A missing local relay must never fail the player build.
                Debug.LogWarning(
                    DiagnosticPrefix + " Could not copy " + ExecutableName
                    + " into the development build: " + exception.Message);
            }
        }

        /// <summary>
        /// Returns whether the terminal binary belongs in a player built for
        /// the given platform and options.
        /// </summary>
        /// <param name="terminalMode">The configured terminal mode.</param>
        /// <param name="platform">The build target.</param>
        /// <param name="options">The build options.</param>
        /// <returns><see langword="true"/> for Windows development builds with the terminal enabled.</returns>
        internal static bool ShouldCopyTerminal(TerminalMode terminalMode, BuildTarget platform, BuildOptions options)
        {
            if (terminalMode == TerminalMode.Off)
            {
                return false;
            }

            if (platform != BuildTarget.StandaloneWindows && platform != BuildTarget.StandaloneWindows64)
            {
                return false;
            }

            return (options & BuildOptions.Development) != 0;
        }

        /// <summary>
        /// Returns the destination the runtime reads:
        /// <c>&lt;player&gt;_Data/StreamingAssets/GlitchPort/glitchport-terminal.exe</c>.
        /// </summary>
        /// <param name="playerPath">The built player executable path.</param>
        /// <returns>The destination path of the terminal executable.</returns>
        internal static string GetDestinationPath(string playerPath)
        {
            var playerDirectory = Path.GetDirectoryName(playerPath);
            if (string.IsNullOrEmpty(playerDirectory))
            {
                playerDirectory = Directory.GetCurrentDirectory();
            }

            var playerName = Path.GetFileNameWithoutExtension(playerPath);
            return Path.Combine(
                playerDirectory!,
                playerName + "_Data/StreamingAssets/GlitchPort/glitchport-terminal.exe");
        }

        private static void CopyTerminal(string playerPath)
        {
            // Re-resolve so a binary built after the editor started is used.
            TerminalToolLocator.Refresh();
            var sourcePath = TerminalToolLocator.ResolvedExecutablePath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return;
            }

            var destinationPath = GetDestinationPath(playerPath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(destinationDirectory))
            {
                return;
            }

            Directory.CreateDirectory(destinationDirectory!);
            File.Copy(sourcePath!, destinationPath, true);
        }

        private static TerminalMode GetConfiguredTerminalMode()
        {
            var settings = AssetDatabase.LoadAssetAtPath<GlitchPortSettings>(
                GlitchPortSettingsProvider.SettingsAssetPath);
            if (settings == null)
            {
                settings = Resources.Load<GlitchPortSettings>(GlitchPortSettingsProvider.SettingsResourceName);
            }

            // Without an asset the runtime falls back to the GlitchPortSettings
            // defaults, whose terminal mode is Auto.
            return settings != null ? settings.TerminalMode : TerminalMode.Auto;
        }
    }
}
