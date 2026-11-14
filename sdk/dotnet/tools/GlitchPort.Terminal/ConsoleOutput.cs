using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GlitchPort.Terminal
{
    /// <summary>A destination that receives already-rendered console text.</summary>
    internal interface IConsoleSink
    {
        /// <summary>Writes <paramref name="text"/> immediately.</summary>
        void Write(string text);
    }

    /// <summary>
    /// A <see cref="TextWriter"/> that forwards every write to an
    /// <see cref="IConsoleSink"/> without buffering.
    /// </summary>
    /// <remarks>
    /// The writer reports <see cref="Encoding.Unicode"/> because a sink such as
    /// <see cref="Win32ConsoleSink"/> hands UTF-16 characters to
    /// <c>WriteConsoleW</c> instead of encoding them into a byte stream.
    /// </remarks>
    internal sealed class ConsoleSinkWriter : TextWriter
    {
        private readonly IConsoleSink _sink;

        /// <summary>Creates a writer that forwards to <paramref name="sink"/>.</summary>
        /// <param name="sink">The destination for every write.</param>
        /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
        public ConsoleSinkWriter(IConsoleSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            _sink = sink;
        }

        /// <inheritdoc/>
        public override Encoding Encoding => Encoding.Unicode;

        /// <inheritdoc/>
        public override void Write(char value)
        {
            _sink.Write(value.ToString());
        }

        /// <inheritdoc/>
        public override void Write(string? value)
        {
            if (value is not null)
            {
                _sink.Write(value);
            }
        }

        /// <inheritdoc/>
        public override void Write(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be non-negative.");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be non-negative.");
            }

            if (buffer.Length - index < count)
            {
                throw new ArgumentException("Index and count were out of bounds for the buffer.", nameof(buffer));
            }

            if (count == 0)
            {
                return;
            }

            _sink.Write(new string(buffer, index, count));
        }

        /// <inheritdoc/>
        public override void WriteLine()
        {
            _sink.Write(NewLine);
        }

        /// <inheritdoc/>
        public override void WriteLine(string? value)
        {
            _sink.Write(string.Concat(value, NewLine));
        }

        /// <summary>Every write reaches the sink immediately, so there is nothing to flush.</summary>
        public override void Flush()
        {
        }
    }

    /// <summary>
    /// Writes text to a Windows console handle with <c>WriteConsoleW</c> so the
    /// console receives UTF-16 characters instead of bytes interpreted with the
    /// active output code page.
    /// </summary>
    internal sealed class Win32ConsoleSink : IConsoleSink
    {
        private readonly IntPtr _handle;

        /// <summary>Wraps an existing console output handle; the handle is not owned.</summary>
        /// <param name="handle">The <c>HANDLE</c> returned by <c>GetStdHandle</c>.</param>
        public Win32ConsoleSink(IntPtr handle)
        {
            _handle = handle;
        }

        /// <inheritdoc/>
        /// <exception cref="IOException">The console rejected the write.</exception>
        public void Write(string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            var remaining = text;
            while (remaining.Length > 0)
            {
                if (!WriteConsoleW(
                        _handle,
                        remaining,
                        (uint)remaining.Length,
                        out var written,
                        IntPtr.Zero)
                    || written == 0)
                {
                    throw new IOException(
                        $"WriteConsoleW failed while writing to the console (Win32 error {Marshal.GetLastWin32Error()}).");
                }

                remaining = remaining.Substring((int)written);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteConsoleW(
            IntPtr hConsoleOutput,
            string lpBuffer,
            uint nNumberOfCharsToWrite,
            out uint lpNumberOfCharsWritten,
            IntPtr lpReserved);
    }

    /// <summary>
    /// Creates the standard output writer: UTF-16 through the console API for a
    /// real Windows console, UTF-8 without a BOM for redirected or non-Windows
    /// output.
    /// </summary>
    internal static class ConsoleOutput
    {
        private const int StandardOutputHandle = -11;
        private static readonly IntPtr InvalidHandleValue = new(-1);

        /// <summary>Creates the writer for the standard output destination.</summary>
        /// <param name="redirectedStream">
        /// The byte stream used when <paramref name="consoleSink"/> is
        /// <see langword="null"/>; it is left untouched otherwise.
        /// </param>
        /// <param name="consoleSink">
        /// The console sink, or <see langword="null"/> when standard output is
        /// redirected or the platform has no Windows console.
        /// </param>
        /// <returns>The writer for standard output.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="redirectedStream"/> is <see langword="null"/> and no
        /// console sink is available.
        /// </exception>
        public static TextWriter Create(Stream redirectedStream, IConsoleSink? consoleSink)
        {
            if (consoleSink is not null)
            {
                return new ConsoleSinkWriter(consoleSink);
            }

            ArgumentNullException.ThrowIfNull(redirectedStream);
            return new StreamWriter(redirectedStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }

        /// <summary>
        /// Returns a sink for the process's standard output handle when it is a
        /// real Windows console; otherwise returns <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// The console mode check rejects redirected pipes and files, which do not
        /// accept <c>WriteConsoleW</c>. The handle is only borrowed, never closed.
        /// </remarks>
        public static IConsoleSink? TryCreateForStandardOutput()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var handle = GetStdHandle(StandardOutputHandle);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue)
            {
                return null;
            }

            if (!GetConsoleMode(handle, out _))
            {
                return null;
            }

            return new Win32ConsoleSink(handle);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    }
}
