using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace GlitchPort.Terminal.Tests
{
    /// <summary>
    /// Process-level tests that launch the real <c>glitchport-terminal</c>
    /// executable built next to the test assembly.
    /// </summary>
    public sealed class TerminalProcessTests
    {
        private const string AppName = "process-test";
        private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

        private const string Payload =
            "{\"event_id\":\"3b7e0c11-1111-2222-3333-444455556666\",\"timestamp\":\"2026-11-14T15:02:14.010Z\",\"level\":\"info\",\"message\":\"hello\",\"sdk\":{\"name\":\"glitchport.dotnet\",\"version\":\"0.1.0\"}}";

        [Fact]
        public async Task WindowNew_RedirectedStdin_ConsumesHelloAndEventAndExitsOnEof()
        {
            RenderResult result = await RunAsync(
                new[] { "--window", "new", "--format", "compact", "--app", AppName },
                HelloLine() + "\n" + EventLine(Payload) + "\n");

            // Exiting proves that the renderer reached EOF on the redirected pipe
            // instead of waiting on a console input source that never reaches EOF.
            Assert.Equal(0, result.ExitCode);

            if (OperatingSystem.IsWindows())
            {
                // Only Windows keeps --window new. Linux and macOS deliberately
                // downgrade it to inherit and print the warning that
                // WindowNew_OnUnsupportedPlatform_WarnsExactlyOnceAndBehavesLikeInherit
                // checks, so the absence of the warning is a Windows-only fact.
                Assert.DoesNotContain(Program.UnsupportedWindowWarning, result.StandardError);
            }

            // Both protocol lines were rendered: the hello line produced the
            // session header and the event line produced the compact event line.
            // Rendering the protocol proves that the renderer consumed the pipe
            // rather than the input buffer of a newly allocated console.
            Assert.Contains("glitch-port terminal", result.StandardOutput);
            Assert.Contains(AppName, result.StandardOutput);
            Assert.Contains("INFO", result.StandardOutput);
            Assert.Contains("hello", result.StandardOutput);
        }

        [Fact]
        public async Task WindowNew_RedirectedStdinEof_ExitsZeroWithoutWaitingForConsoleInput()
        {
            RenderResult result = await RunAsync(
                new[] { "--window", "new", "--format", "compact" },
                string.Empty);

            // The redirected pipe reaches EOF immediately. A renderer that switched
            // its input to the new console would never observe that EOF and could
            // not return, so a prompt exit proves that stdin still comes from the
            // pipe rather than from the console.
            Assert.Equal(0, result.ExitCode);
        }

        [Fact]
        public async Task WindowInherit_RedirectedStdin_RendersHelloAndEventAndExitsOnEof()
        {
            RenderResult result = await RunAsync(
                new[] { "--window", "inherit", "--format", "compact", "--app", AppName },
                HelloLine() + "\n" + EventLine(Payload) + "\n");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
            Assert.Contains("glitch-port terminal", result.StandardOutput);
            Assert.Contains("INFO", result.StandardOutput);
            Assert.Contains("hello", result.StandardOutput);
        }

        [Fact]
        public async Task WindowNew_OnUnsupportedPlatform_WarnsExactlyOnceAndBehavesLikeInherit()
        {
            if (OperatingSystem.IsWindows())
            {
                // The fallback itself is covered by
                // ProgramTests.ResolveWindowMode_NewOnUnsupportedPlatform_*.
                return;
            }

            RenderResult result = await RunAsync(
                new[] { "--window", "new", "--format", "compact", "--app", AppName },
                HelloLine() + "\n" + EventLine(Payload) + "\n");

            Assert.Equal(0, result.ExitCode);

            var warningLines = result.StandardError.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Assert.Single(warningLines);
            Assert.Equal(Program.UnsupportedWindowWarning, warningLines[0]);

            // Apart from the warning, --window new behaves exactly like inherit:
            // it reads the redirected pipe and renders to the redirected output.
            Assert.Contains("glitch-port terminal", result.StandardOutput);
            Assert.Contains("INFO", result.StandardOutput);
            Assert.Contains("hello", result.StandardOutput);
        }

        [Fact]
        public async Task RedirectedStandardOutput_WritesUtf8WithoutBomAndPreservesUnicode()
        {
            if (!OperatingSystem.IsWindows())
            {
                // Console detection uses the Windows GetConsoleMode API; the UTF-8
                // no-BOM fallback itself is covered by ConsoleOutputTests on every
                // platform.
                return;
            }

            const string payload =
                "{\"event_id\":\"3b7e0c11-1111-2222-3333-444455556666\",\"timestamp\":\"2026-11-14T15:02:14.010Z\",\"level\":\"info\",\"message\":\"안녕 hello\",\"sdk\":{\"name\":\"glitchport.dotnet\",\"version\":\"0.1.0\"}}";

            var startInfo = CreateStartInfo(new[] { "--window", "inherit", "--format", "compact", "--app", AppName });
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
            {
                throw new XunitException("The glitchport-terminal process did not start.");
            }

            // Read raw bytes: decoding through the StandardOutput string reader
            // would hide a UTF-16 or BOM-prefixed stream and let a wrong console
            // output path pass.
            Task<byte[]> standardOutput = ReadAllBytesAsync(process.StandardOutput.BaseStream);
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            try
            {
                // The renderer always decodes stdin as UTF-8, so the protocol
                // input must not depend on the platform's default input encoding.
                var input = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes(HelloLine() + "\n" + EventLine(payload) + "\n");

                await process.StandardInput.BaseStream.WriteAsync(input);
                await process.StandardInput.BaseStream.FlushAsync();

                // EOF is what makes the renderer drain the protocol and return 0.
                process.StandardInput.Close();

                if (!await WaitForExitAsync(process, ExitTimeout))
                {
                    Kill(process);

                    string partialError = await TryReadAsync(standardError);

                    throw new XunitException(
                        "glitchport-terminal did not exit within "
                        + ExitTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                        + " seconds with a redirected standard output; stderr: '"
                        + partialError
                        + "'");
                }

                byte[] bytes = await standardOutput;

                // With standard output redirected, GetConsoleMode fails for the
                // pipe handle, so ConsoleOutput.TryCreateForStandardOutput() must
                // return null and the renderer must use the UTF-8 StreamWriter.
                // This test is the observable equivalent of that null check: a
                // stray WriteConsoleW path would fail or emit nothing, and the
                // strict UTF-8 decode rejects UTF-16 or BOM-prefixed bytes.
                string output = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(bytes);

                Assert.Equal(0, process.ExitCode);
                Assert.Equal(string.Empty, await standardError);
                Assert.False(HasUtf8Bom(bytes));
                Assert.True(
                    ContainsSequence(bytes, new byte[] { 0xE2, 0x95, 0x90 }),
                    "The raw standard output did not contain the UTF-8 encoding of U+2550.");
                Assert.Contains("═", output, StringComparison.Ordinal);
                Assert.Contains('·', output);
                Assert.Contains("안녕 hello", output, StringComparison.Ordinal);
                Assert.DoesNotContain('\uFFFD', output);
            }
            finally
            {
                if (!HasExited(process))
                {
                    Kill(process);
                }
            }
        }

        private static async Task<RenderResult> RunAsync(string[] arguments, string standardInput)
        {
            using var process = new Process { StartInfo = CreateStartInfo(arguments) };

            if (!process.Start())
            {
                throw new XunitException("The glitchport-terminal process did not start.");
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            try
            {
                if (standardInput.Length > 0)
                {
                    await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                }

                // EOF is what makes the renderer drain the protocol and return 0.
                process.StandardInput.Close();

                if (!await WaitForExitAsync(process, ExitTimeout).ConfigureAwait(false))
                {
                    Kill(process);

                    string partialOutput = await TryReadAsync(standardOutput).ConfigureAwait(false);
                    string partialError = await TryReadAsync(standardError).ConfigureAwait(false);

                    throw new XunitException(
                        "glitchport-terminal did not exit within "
                        + ExitTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                        + " seconds; it may be reading the new console instead of the redirected stdin. "
                        + "stdout: '"
                        + partialOutput
                        + "' stderr: '"
                        + partialError
                        + "'");
                }

                return new RenderResult(
                    process.ExitCode,
                    await standardOutput.ConfigureAwait(false),
                    await standardError.ConfigureAwait(false));
            }
            finally
            {
                if (!HasExited(process))
                {
                    Kill(process);
                }
            }
        }

        private static ProcessStartInfo CreateStartInfo(string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false,
            };

            var nativeExecutable = Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "glitchport-terminal.exe" : "glitchport-terminal");

            if (File.Exists(nativeExecutable))
            {
                startInfo.FileName = nativeExecutable;
            }
            else
            {
                // Project references do not always copy the native apphost on
                // every platform; the framework-dependent assembly is equivalent.
                var managedAssembly = Path.Combine(AppContext.BaseDirectory, "glitchport-terminal.dll");
                Assert.True(
                    File.Exists(managedAssembly),
                    "glitchport-terminal was not found next to the test assembly: '"
                        + nativeExecutable
                        + "' or '"
                        + managedAssembly
                        + "'.");

                startInfo.FileName = "dotnet";
                startInfo.ArgumentList.Add(managedAssembly);
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);

            try
            {
                await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static async Task<string> TryReadAsync(Task<string> read)
        {
            try
            {
                return await read.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return "<unavailable>";
            }
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer).ConfigureAwait(false);
            return buffer.ToArray();
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private static bool ContainsSequence(byte[] bytes, byte[] sequence)
        {
            return bytes.AsSpan().IndexOf(sequence) >= 0;
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (Exception)
            {
                // The process may have exited between the checks.
            }
        }

        private static string HelloLine()
        {
            var builder = new StringBuilder();
            builder.Append("{\"type\":\"hello\",\"protocol\":1,\"parent_pid\":4242,\"app\":\"");
            builder.Append(AppName);
            builder.Append("\"}");
            return builder.ToString();
        }

        private static string EventLine(string payload)
        {
            return "{\"type\":\"event\",\"payload\":" + payload + "}";
        }

        private readonly record struct RenderResult(int ExitCode, string StandardOutput, string StandardError);
    }
}
