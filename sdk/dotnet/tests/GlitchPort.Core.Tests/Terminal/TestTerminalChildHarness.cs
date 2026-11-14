using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    /// <summary>How the fake terminal child treats its standard input.</summary>
    internal enum TestTerminalChildMode
    {
        /// <summary>Read stdin until EOF and record every byte (the normal renderer).</summary>
        Read,

        /// <summary>Never read stdin and never exit on its own (a stuck renderer).</summary>
        Ignore,
    }

    /// <summary>
    /// A per-test copy of <c>GlitchPort.TestTerminalChild</c> plus the files it
    /// records. Copying the executable into a private directory lets every test
    /// run in parallel: the child writes its arguments, start marker, mode and
    /// raw stdin next to itself and never relies on process-wide environment
    /// variables.
    /// </summary>
    internal sealed class TestTerminalChildRecording : IDisposable
    {
        private const string ArgumentsFileName = "args.txt";
        private const string StartsFileName = "starts.txt";
        private const string StandardInputFileName = "stdin.bin";

        /// <summary>File next to the executable that selects the stdin behaviour.</summary>
        internal const string ModeFileName = "mode.txt";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        internal TestTerminalChildRecording(string directoryPath, string executablePath)
        {
            DirectoryPath = directoryPath;
            ExecutablePath = executablePath;
        }

        /// <summary>Gets the private directory the child executable was copied to.</summary>
        public string DirectoryPath { get; }

        /// <summary>Gets the executable the relay must start.</summary>
        public string ExecutablePath { get; }

        public string ArgumentsPath => Path.Combine(DirectoryPath, ArgumentsFileName);

        public string StartsPath => Path.Combine(DirectoryPath, StartsFileName);

        public string StandardInputPath => Path.Combine(DirectoryPath, StandardInputFileName);

        /// <summary>Gets the command line arguments the child received, one per element.</summary>
        public string[] ReadArguments()
        {
            return SplitLines(ReadAllTextShared(ArgumentsPath));
        }

        /// <summary>Gets one child pid per start attempt; proves no replacement child was started.</summary>
        public string[] ReadStartedProcessIds()
        {
            return SplitLines(ReadAllTextShared(StartsPath));
        }

        /// <summary>Gets the exact bytes the child read from standard input.</summary>
        public byte[] ReadStandardInputBytes()
        {
            return ReadAllBytesShared(StandardInputPath);
        }

        /// <summary>Gets the UTF-8 text of the bytes the child read from standard input.</summary>
        public string ReadStandardInputText()
        {
            return Utf8NoBom.GetString(ReadStandardInputBytes());
        }

        /// <summary>Gets the protocol lines the child received, without the trailing newline.</summary>
        public string[] ReadStandardInputLines()
        {
            return SplitLines(ReadStandardInputText());
        }

        public void Dispose()
        {
            TryDeleteDirectory(DirectoryPath);
        }

        private static string ReadAllTextShared(string path)
        {
            var bytes = ReadAllBytesShared(path);
            return Utf8NoBom.GetString(bytes);
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            if (!File.Exists(path))
            {
                return Array.Empty<byte>();
            }

            try
            {
                // The child keeps stdin.bin open while it runs, so the reader must
                // share write access (File.ReadAllBytes would request FileShare.Read
                // and fail with a sharing violation on Windows).
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (IOException)
            {
                return Array.Empty<byte>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<byte>();
            }
        }

        private static string[] SplitLines(string text)
        {
            if (text.Length == 0)
            {
                return Array.Empty<string>();
            }

            var trimmed = text.TrimEnd('\n');
            if (trimmed.Length == 0)
            {
                return Array.Empty<string>();
            }

            return trimmed.Split('\n');
        }

        private static void TryDeleteDirectory(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception)
            {
                // The temporary directory is small; a leaked copy must not fail a test.
            }
        }
    }

    /// <summary>Creates fake terminal children and polls their recordings.</summary>
    internal static class TestTerminalChild
    {
        private const string SourceDirectoryName = "test-terminal-child";
        private const string ExecutableBaseName = "GlitchPort.TestTerminalChild";

        /// <summary>
        /// Copies the test child into a fresh directory and configures its stdin
        /// behaviour. The executable ignores all terminal-only arguments it
        /// receives from the relay.
        /// </summary>
        public static TestTerminalChildRecording Create(TestTerminalChildMode mode = TestTerminalChildMode.Read)
        {
            var sourceDirectory = Path.Combine(AppContext.BaseDirectory, SourceDirectoryName);
            Assert.True(
                Directory.Exists(sourceDirectory),
                "The fake terminal child was not copied to '" + sourceDirectory
                + "'. Build GlitchPort.Core.Tests so the CopyTestTerminalChild target can run.");

            var executableName = ExecutableBaseName
                + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);
            var sourceExecutable = Path.Combine(sourceDirectory, executableName);

            Assert.True(
                File.Exists(sourceExecutable),
                "The fake terminal child executable '" + sourceExecutable + "' does not exist.");

            var directory = CreateTempDirectory();

            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }

            var executablePath = Path.Combine(directory, executableName);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Defensive: keep the apphost executable on Unix even if the copy
                // implementation did not preserve the file mode.
                File.SetUnixFileMode(executablePath, File.GetUnixFileMode(sourceExecutable));
            }

            if (mode == TestTerminalChildMode.Ignore)
            {
                // The child reads this file next to its executable; "ignore" makes
                // it stay alive without ever consuming the stdin pipe.
                File.WriteAllText(
                    Path.Combine(directory, TestTerminalChildRecording.ModeFileName),
                    "ignore\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            return new TestTerminalChildRecording(directory, executablePath);
        }

        /// <summary>Waits until the child wrote its start marker (args are complete by then).</summary>
        public static async Task<bool> WaitForStartAsync(
            TestTerminalChildRecording recording,
            TimeSpan timeout)
        {
            return await TerminalTestProcess.WaitUntilAsync(
                () => recording.ReadStartedProcessIds().Length > 0,
                timeout).ConfigureAwait(false);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "glitchport-terminal-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>Process and polling helpers shared by the terminal relay tests.</summary>
    internal static class TerminalTestProcess
    {
        /// <summary>The upper bound the acceptance criteria put on a Dispose call.</summary>
        public static readonly TimeSpan DisposeBudget = TimeSpan.FromSeconds(5);

        public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(20).ConfigureAwait(false);
            }

            return condition();
        }

        /// <summary>
        /// Awaits <paramref name="task"/> at most <paramref name="timeout"/> and
        /// reports whether it completed. The timer is cancelled so a blocked task
        /// does not leave a delayed task behind.
        /// </summary>
        public static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
        {
            if (task.IsCompleted)
            {
                return true;
            }

            using var cancellation = new CancellationTokenSource();
            var delay = Task.Delay(timeout, cancellation.Token);
            var winner = await Task.WhenAny(task, delay).ConfigureAwait(false);

            if (winner == task)
            {
                cancellation.Cancel();
                return true;
            }

            return false;
        }

        public static bool IsProcessAlive(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public static async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout)
        {
            return await WaitUntilAsync(() => !IsProcessAlive(processId), timeout).ConfigureAwait(false);
        }

        public static void KillIfAlive(int? processId)
        {
            if (processId is null)
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(processId.Value);
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // Cleanup must never fail a test; the assertion reports leaks.
            }
        }
    }
}
