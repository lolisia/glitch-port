namespace GlitchPort.Terminal
{
    /// <summary>Options that control how <see cref="TerminalHost"/> renders the relay stream.</summary>
    public sealed class TerminalHostOptions
    {
        /// <summary>Gets or sets the fallback application name shown in the session header.</summary>
        public string AppName { get; set; } = "";

        /// <summary>Gets or sets the output format. Defaults to <see cref="TerminalHostFormat.Pretty"/>.</summary>
        public TerminalHostFormat Format { get; set; } = TerminalHostFormat.Pretty;

        /// <summary>Gets or sets a value indicating whether ANSI colour escapes are emitted.</summary>
        public bool Color { get; set; }

        /// <summary>
        /// Gets or sets the pid of the parent process to watch. When
        /// <see langword="null"/> the parent is not watched.
        /// </summary>
        public int? ParentProcessId { get; set; }
    }

    /// <summary>Output format used by <see cref="TerminalHost"/>.</summary>
    public enum TerminalHostFormat
    {
        /// <summary>Multi-line event rendering with an sdk/id footer.</summary>
        Pretty = 0,

        /// <summary>One line per event.</summary>
        Compact = 1,

        /// <summary>The raw event payload exactly as received.</summary>
        Json = 2,
    }
}
