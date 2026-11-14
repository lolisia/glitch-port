using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace GlitchPort
{
    /// <summary>
    /// Resolves the <c>glitchport-terminal</c> executable used by the terminal relay.
    /// </summary>
    /// <remarks>
    /// Candidates are tried in order and the first existing file wins:
    /// <list type="number">
    /// <item><see cref="TerminalRelayOptions.ExecutablePath"/>;</item>
    /// <item>the <c>GLITCHPORT_TERMINAL_PATH</c> environment variable;</item>
    /// <item><c>AppContext.BaseDirectory/glitchport-terminal(.exe)</c>;</item>
    /// <item><c>AppContext.BaseDirectory/runtimes/&lt;rid&gt;/native/glitchport-terminal(.exe)</c>.</item>
    /// </list>
    /// </remarks>
    internal static class TerminalProcessLocator
    {
        /// <summary>The executable file name without its platform suffix.</summary>
        public const string ExecutableName = "glitchport-terminal";

        /// <summary>The environment variable consulted after the explicit option.</summary>
        public const string EnvironmentVariableName = "GLITCHPORT_TERMINAL_PATH";

        /// <summary>
        /// Resolves the executable, or returns <see langword="null"/> when no
        /// candidate exists.
        /// </summary>
        /// <param name="options">The terminal relay options.</param>
        public static string? Resolve(TerminalRelayOptions options)
        {
            var locations = GetSearchLocations(options);

            for (var i = 0; i < locations.Count; i++)
            {
                var candidate = locations[i];

                // File.Exists never throws: invalid paths and I/O errors simply
                // report false, which is exactly what the search wants.
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the executable candidates in resolution order. Unset option and
        /// environment values are omitted. Used for the "not found" error message.
        /// </summary>
        /// <param name="options">The terminal relay options.</param>
        public static IReadOnlyList<string> GetSearchLocations(TerminalRelayOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var locations = new List<string>(4);

            if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
            {
                locations.Add(options.ExecutablePath!);
            }

            var environmentPath = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(environmentPath))
            {
                locations.Add(environmentPath!);
            }

            var suffix = GetExecutableSuffix();
            locations.Add(Path.Combine(AppContext.BaseDirectory, ExecutableName + suffix));

            var runtimeIdentifier = GetRuntimeIdentifier();
            if (runtimeIdentifier is not null)
            {
                locations.Add(Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
                    runtimeIdentifier,
                    "native",
                    ExecutableName + suffix));
            }

            return locations;
        }

        /// <summary>Returns <c>.exe</c> on Windows and an empty string elsewhere.</summary>
        public static string GetExecutableSuffix()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;
        }

        /// <summary>
        /// Builds a .NET runtime identifier such as <c>win-x64</c> or
        /// <c>osx-arm64</c>, or <see langword="null"/> for an unknown platform.
        /// </summary>
        public static string? GetRuntimeIdentifier()
        {
            string operatingSystem;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                operatingSystem = "win";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                operatingSystem = "linux";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                operatingSystem = "osx";
            }
            else
            {
                return null;
            }

            string architecture;

            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    architecture = "x64";
                    break;
                case Architecture.X86:
                    architecture = "x86";
                    break;
                case Architecture.Arm:
                    architecture = "arm";
                    break;
                case Architecture.Arm64:
                    architecture = "arm64";
                    break;
                default:
                    return null;
            }

            return operatingSystem + "-" + architecture;
        }
    }
}
