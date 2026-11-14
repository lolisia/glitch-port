using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GlitchPort.TestTerminalChild
{
    /// <summary>
    /// Fake <c>glitchport-terminal</c> used by the terminal relay tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The child is started by <c>TerminalRelay</c> with the terminal-only
    /// arguments (<c>--parent-pid</c>, <c>--format</c>, <c>--window</c> and
    /// <c>--app</c>). It deliberately ignores them and records the arguments it
    /// was started with, so a test can assert the exact command line without
    /// involving <c>cmd.exe</c> or <c>/bin/sh</c>.
    /// </para>
    /// <para>
    /// Everything is written next to the executable (which the test copies into
    /// a per-test directory):
    /// <list type="bullet">
    /// <item><c>args.txt</c> - one command line argument per line, UTF-8, LF.</item>
    /// <item><c>starts.txt</c> - one line with the child pid per start, appended.</item>
    /// <item><c>stdin.bin</c> - the raw bytes read from standard input.</item>
    /// <item><c>mode.txt</c> - test input: <c>read</c> (default) or <c>ignore</c>.</item>
    /// </list>
    /// In <c>ignore</c> mode the child never reads standard input and never
    /// exits by itself: it is the non-reading renderer used to fill the relay's
    /// stdin pipe.
    /// </para>
    /// </remarks>
    internal static class Program
    {
        private const string ReadMode = "read";
        private const string IgnoreMode = "ignore";

        private const string ArgumentsFileName = "args.txt";
        private const string StartsFileName = "starts.txt";
        private const string StandardInputFileName = "stdin.bin";
        private const string ModeFileName = "mode.txt";

        internal static int Main(string[] args)
        {
            var recordDirectory = AppContext.BaseDirectory;

            try
            {
                WriteLines(Path.Combine(recordDirectory, ArgumentsFileName), args);
                AppendLine(Path.Combine(recordDirectory, StartsFileName), GetCurrentProcessId());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("test terminal child: " + ex.Message);
                return 3;
            }

            var mode = ReadConfiguredMode(Path.Combine(recordDirectory, ModeFileName));

            if (string.Equals(mode, IgnoreMode, StringComparison.OrdinalIgnoreCase))
            {
                // Never read stdin and never exit on our own; the relay has to
                // kill the process during shutdown.
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }

            try
            {
                CopyStandardInputToFile(Path.Combine(recordDirectory, StandardInputFileName));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("test terminal child: " + ex.Message);
                return 4;
            }

            return 0;
        }

        private static string GetCurrentProcessId()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.Id.ToString(CultureInfo.InvariantCulture);
        }

        private static string ReadConfiguredMode(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : ReadMode;
            }
            catch (IOException)
            {
                return ReadMode;
            }
            catch (UnauthorizedAccessException)
            {
                return ReadMode;
            }
        }

        /// <summary>
        /// Drains standard input until EOF and stores the exact bytes. The file is
        /// opened with <see cref="FileShare.ReadWrite"/> so tests can poll it while
        /// the child is still running.
        /// </summary>
        private static void CopyStandardInputToFile(string path)
        {
            using var input = Console.OpenStandardInput();
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

            var buffer = new byte[8192];

            while (true)
            {
                int read;

                try
                {
                    read = input.Read(buffer, 0, buffer.Length);
                }
                catch (IOException)
                {
                    // A broken pipe is a normal shutdown path for the relay.
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                output.Flush();
            }
        }

        private static void WriteLines(string path, string[] lines)
        {
            using var writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                NewLine = "\n",
            };

            for (var i = 0; i < lines.Length; i++)
            {
                writer.WriteLine(lines[i]);
            }
        }

        private static void AppendLine(string path, string line)
        {
            using var writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                NewLine = "\n",
            };

            writer.WriteLine(line);
        }
    }
}
