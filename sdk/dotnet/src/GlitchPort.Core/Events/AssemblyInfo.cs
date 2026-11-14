using System.Runtime.CompilerServices;

// The Core public API is intentionally small; the test project exercises the
// internal JSON writer, serializer and protocol limits directly.
[assembly: InternalsVisibleTo("GlitchPort.Core.Tests")]
