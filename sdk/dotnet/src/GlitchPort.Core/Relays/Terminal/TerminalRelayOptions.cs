using System;
using System.Diagnostics;

namespace GlitchPort
{
    /// <summary>How the terminal renderer obtains its console window.</summary>
    public enum TerminalWindow
    {
        /// <summary>Use the console inherited from the parent process.</summary>
        Inherit = 0,

        /// <summary>Open a fresh console window (Windows only).</summary>
        New = 1,
    }

    /// <summary>Event rendering format understood by <c>glitchport-terminal</c>.</summary>
    public enum TerminalFormat
    {
        /// <summary>Multi-line event rendering.</summary>
        Pretty = 0,

        /// <summary>One line per event.</summary>
        Compact = 1,

        /// <summary>The raw event payload.</summary>
        Json = 2,
    }

    /// <summary>Options for the local terminal relay.</summary>
    /// <remarks>
    /// The relay starts <c>glitchport-terminal</c> as a child process on the first
    /// captured event and writes the line protocol to its standard input.
    /// </remarks>
    public sealed class TerminalRelayOptions : RelayOptions
    {
        /// <summary>Creates the default terminal relay options.</summary>
        public TerminalRelayOptions()
        {
            AppName = TryGetCurrentProcessName();
        }

        /// <summary>
        /// Gets or sets the path to the <c>glitchport-terminal</c> executable. When
        /// <see langword="null"/> the relay falls back to
        /// <c>GLITCHPORT_TERMINAL_PATH</c> and then to the application directory.
        /// </summary>
        public string? ExecutablePath { get; set; }

        /// <summary>Gets or sets the window mode. Defaults to <see cref="TerminalWindow.Inherit"/>.</summary>
        public TerminalWindow Window { get; set; } = TerminalWindow.Inherit;

        /// <summary>Gets or sets the output format. Defaults to <see cref="TerminalFormat.Pretty"/>.</summary>
        public TerminalFormat Format { get; set; } = TerminalFormat.Pretty;

        /// <summary>
        /// Gets or sets the application name shown by the terminal. Defaults to the
        /// current process name.
        /// </summary>
        public string? AppName { get; set; }

        private static string? TryGetCurrentProcessName()
        {
            try
            {
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    return currentProcess.ProcessName;
                }
            }
            catch (Exception)
            {
                // Process information is unavailable on some restricted platforms;
                // the relay falls back when it builds the hello line.
                return null;
            }
        }
    }
}
