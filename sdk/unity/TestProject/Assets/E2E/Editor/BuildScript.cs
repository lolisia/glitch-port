#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GlitchPort.TestProject.Editor
{
    /// <summary>
    /// Builds the Windows development player used by <c>scripts/e2e-unity.sh</c>.
    /// The player uses the Mono scripting backend and contains only the e2e
    /// scene, so the script can run it headlessly right after the build.
    /// </summary>
    public static class BuildScript
    {
        /// <summary>The command line argument that overrides the output executable path.</summary>
        private const string OutputArgument = "-buildOutput";

        /// <summary>The scene <see cref="E2EDriver"/> runs in.</summary>
        private const string ScenePath = "Assets/E2E/E2EScene.unity";

        /// <summary>The executable name used when no <c>-buildOutput</c> argument is given.</summary>
        private const string DefaultOutputPath = "Build/Windows/GlitchPortE2E.exe";

        /// <summary>
        /// Builds the test project as a Windows 64-bit development player with
        /// the Mono scripting backend. Unity exits with 0 when the player exists
        /// and with 1 (after logging the reason) otherwise.
        /// </summary>
        public static void BuildWindowsDevelopment()
        {
            try
            {
                var outputPath = Path.GetFullPath(ReadOutputPath());
                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                EnsureMonoScriptingBackend();

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = outputPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development,
                };

                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;

                if (summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError(
                        "[GlitchPort] E2E player build failed: "
                        + summary.result
                        + " ("
                        + summary.totalErrors
                        + " errors)");
                    EditorApplication.Exit(1);
                    return;
                }

                if (!File.Exists(outputPath))
                {
                    Debug.LogError(
                        "[GlitchPort] E2E player build succeeded but " + outputPath + " does not exist.");
                    EditorApplication.Exit(1);
                    return;
                }

                Debug.Log("[GlitchPort] E2E player built at " + outputPath);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError("[GlitchPort] E2E player build failed: " + exception);
                EditorApplication.Exit(1);
            }
        }

        private static void EnsureMonoScriptingBackend()
        {
            // Setting the backend explicitly writes a per-platform override into
            // ProjectSettings.asset; Mono is already the default for standalone
            // players, so only persist the override when it is actually needed.
            if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone)
                != ScriptingImplementation.Mono2x)
            {
                PlayerSettings.SetScriptingBackend(
                    NamedBuildTarget.Standalone,
                    ScriptingImplementation.Mono2x);
            }
        }

        private static string ReadOutputPath()
        {
            var arguments = Environment.GetCommandLineArgs();

            for (var i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], OutputArgument, StringComparison.Ordinal))
                {
                    return arguments[i + 1];
                }
            }

            return DefaultOutputPath;
        }
    }
}
