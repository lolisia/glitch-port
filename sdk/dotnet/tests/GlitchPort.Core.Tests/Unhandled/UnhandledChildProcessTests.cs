using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests.Unhandled
{
    public sealed class UnhandledChildProcessTests
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task ChildProcess_UnhandledException_WritesAFatalEventToTheFileRelay()
        {
            var directory = CreateTempDirectory();

            try
            {
                var executable = ResolveChildExecutable();

                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add(directory);

                using var process = Process.Start(startInfo);
                Assert.NotNull(process);

                var standardOutput = process!.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();

                var exited = await WaitForExitAsync(process, ProcessTimeout);
                if (!exited)
                {
                    TryKill(process);
                    Assert.Fail("The unhandled-exception child did not exit within " + ProcessTimeout + ".");
                }

                Assert.NotEqual(0, process.ExitCode);

                var file = await WaitForLogFileAsync(directory, FileTimeout);
                Assert.True(
                    file is not null,
                    "No file relay output was written. stdout: "
                        + await standardOutput
                        + " stderr: "
                        + await standardError);

                var line = Assert.Single(ReadLinesShared(file!));
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                Assert.Equal("fatal", root.GetProperty("level").GetString());
                Assert.Equal("unhandled-child@1.0.0", root.GetProperty("release").GetString());
                Assert.Equal("test", root.GetProperty("environment").GetString());
                Assert.Equal("unhandled-child", root.GetProperty("server_name").GetString());

                var exception = root.GetProperty("exception")[0];
                Assert.Equal("System.InvalidOperationException", exception.GetProperty("type").GetString());
                Assert.Equal("unhandled child boom", exception.GetProperty("value").GetString());
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        private static string ResolveChildExecutable()
        {
            var name = "GlitchPort.TestUnhandledChild"
                + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);
            var path = Path.Combine(AppContext.BaseDirectory, "test-unhandled-child", name);

            Assert.True(
                File.Exists(path),
                "The unhandled-exception child was not copied to '" + path
                + "'. Build GlitchPort.Core.Tests so the CopyTestUnhandledChild target can run.");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Keep the apphost executable even when the copy implementation
                // did not preserve the file mode (mirrors the terminal harness).
                File.SetUnixFileMode(
                    path,
                    File.GetUnixFileMode(path)
                        | UnixFileMode.UserExecute
                        | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherExecute);
            }

            return path;
        }

        private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);

            try
            {
                await process.WaitForExitAsync(cancellation.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return process.HasExited;
            }
        }

        private static async Task<string?> WaitForLogFileAsync(string directory, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                var files = Directory.GetFiles(directory, "glitchport-*.ndjson");
                if (files.Length > 0 && ReadLinesShared(files[0]).Length > 0)
                {
                    return files[0];
                }

                await Task.Delay(50);
            }

            return null;
        }

        private static string[] ReadLinesShared(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var text = reader.ReadToEnd().TrimEnd('\n', '\r');
                return text.Length == 0 ? Array.Empty<string>() : text.Split('\n');
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // Test cleanup is best effort; the assertion reports the failure.
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "glitchport-unhandled-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception)
            {
                // A leaked temporary directory must not fail the test.
            }
        }
    }
}
