namespace GlitchPort
{
    /// <summary>One stack frame of an <see cref="ExceptionInfo"/>.</summary>
    public sealed class StackFrameInfo
    {
        /// <summary>Gets or sets the function or method name.</summary>
        public string? Function { get; set; }

        /// <summary>Gets or sets the module, assembly or namespace that contains the frame.</summary>
        public string? Module { get; set; }

        /// <summary>Gets or sets the source file name as reported by the runtime.</summary>
        public string? FileName { get; set; }

        /// <summary>Gets or sets the absolute path of the source file, when available.</summary>
        public string? AbsPath { get; set; }

        /// <summary>Gets or sets the 1-based line number, or <see langword="null"/> when unavailable.</summary>
        public int? LineNumber { get; set; }

        /// <summary>Gets or sets the 1-based column number, or <see langword="null"/> when unavailable.</summary>
        public int? ColumnNumber { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the frame belongs to the game's
        /// own code rather than engine or framework code.
        /// </summary>
        public bool InApp { get; set; }
    }
}
