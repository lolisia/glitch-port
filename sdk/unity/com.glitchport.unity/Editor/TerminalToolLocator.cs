#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GlitchPort.Unity.Editor
{
    /// <summary>
    /// Locates the <c>glitchport-terminal</c> executable shipped inside the
    /// package and publishes it to the runtime through
    /// <see cref="UnityPlatformInfo.TerminalExecutablePathOverride"/>.
    /// </summary>
    /// <remarks>
    /// The binary lives in <c>Tools~/win-x64/</c>, a folder Unity does not
    /// import, and is produced by <c>scripts/build-unity-plugins.sh</c>. The
    /// build postprocessor re-runs <see cref="Refresh"/> so a binary built while
    /// the editor was open is picked up.
    /// </remarks>
    [InitializeOnLoad]
    public static class TerminalToolLocator
    {
        private const string DiagnosticPrefix = "[GlitchPort]";
        private const string ExecutableRelativePath = "Tools~/win-x64/glitchport-terminal.exe";
        private const string ExecutableName = "glitchport-terminal.exe";
        private const string BuildScript = "scripts/build-unity-plugins.sh";
        private const string WarningSessionKey = "GlitchPort.TerminalLocatorWarningLogged";

        private static string? _resolvedExecutablePath;

        static TerminalToolLocator()
        {
            Refresh();
        }

        /// <summary>
        /// Gets the executable path resolved by the last refresh, or
        /// <see langword="null"/> when the package has no Windows terminal
        /// binary or the editor is not running on Windows.
        /// </summary>
        internal static string? ResolvedExecutablePath => _resolvedExecutablePath;

        /// <summary>
        /// Resolves the packaged terminal executable again, updates
        /// <see cref="UnityPlatformInfo.TerminalExecutablePathOverride"/>, and
        /// returns the resolved path.
        /// </summary>
        /// <returns>The executable path, or <see langword="null"/> when it is unavailable.</returns>
        internal static string? Refresh()
        {
            string? executablePath = null;

            try
            {
                executablePath = Resolve();
            }
            catch (Exception exception)
            {
                LogWarningOnce(DiagnosticPrefix + " Could not locate " + ExecutableName + ": " + exception.Message);
            }

            _resolvedExecutablePath = executablePath;

            // The override is the only value the runtime reads; setting it to
            // null on non-Windows editors keeps the terminal relay disabled
            // without a warning.
            UnityPlatformInfo.TerminalExecutablePathOverride = executablePath;
            return executablePath;
        }

        private static string? Resolve()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                return null;
            }

            var packagePath = FindPackagePath();
            if (string.IsNullOrEmpty(packagePath))
            {
                return null;
            }

            var executablePath = Path.Combine(packagePath!, ExecutableRelativePath);
            if (File.Exists(executablePath))
            {
                return executablePath;
            }

            LogWarningOnce(
                DiagnosticPrefix + " " + ExecutableName + " was not found at " + executablePath
                + ". Run " + BuildScript + " to build the Unity plugins.");
            return null;
        }

        private static string? FindPackagePath()
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(GlitchPortSettings).Assembly);
                return package != null ? package.resolvedPath : null;
            }
            catch (Exception exception)
            {
                LogWarningOnce(
                    DiagnosticPrefix + " Could not resolve the com.glitchport.unity package path: "
                    + exception.Message);
                return null;
            }
        }

        private static void LogWarningOnce(string message)
        {
            // SessionState survives the domain reloads that follow a script
            // compile, so the build-script hint is only printed once per
            // editor session instead of on every reload.
            if (SessionState.GetBool(WarningSessionKey, false))
            {
                return;
            }

            SessionState.SetBool(WarningSessionKey, true);
            Debug.LogWarning(message);
        }
    }
}
