using System.Collections.Generic;

namespace GlitchPort
{
    /// <summary>
    /// One exception in the event's exception chain, ordered from the innermost
    /// cause to the outermost observed exception.
    /// </summary>
    public sealed class ExceptionInfo
    {
        /// <summary>Gets or sets the exception type, for example <c>System.InvalidOperationException</c>.</summary>
        public string? Type { get; set; }

        /// <summary>Gets or sets the exception message.</summary>
        public string? Value { get; set; }

        /// <summary>Gets or sets the assembly, module or namespace that defines the exception.</summary>
        public string? Module { get; set; }

        /// <summary>
        /// Gets or sets the stack frames ordered from the outermost caller
        /// (index 0) to the crash site (last item).
        /// </summary>
        public IList<StackFrameInfo> Frames { get; set; } = new List<StackFrameInfo>();
    }
}
