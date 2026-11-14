using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class TerminalRelayTests
    {
        private const string TerminalPathVariable = "GLITCHPORT_TERMINAL_PATH";
        private const string DummyExecutable = "this file is not a real executable";

        [Fact]
        public void TerminalRelayOptions_UseTheDocumentedDefaults()
        {
            var options = new TerminalRelayOptions();

            Assert.Equal(TerminalWindow.Inherit, options.Window);
            Assert.Equal(TerminalFormat.Pretty, options.Format);
            Assert.True(options.Enabled);
            Assert.Equal(Level.Debug, options.MinLevel);
            Assert.Equal(1000, options.QueueCapacity);
            Assert.Null(options.ExecutablePath);
            Assert.False(string.IsNullOrWhiteSpace(options.AppName));
        }

        [Fact]
        public void RelayTerminal_UsesTheTerminalDefaults()
        {
            var definition = Relay.Terminal();

            Assert.Equal("terminal", definition.Type);
            Assert.Equal("terminal", definition.Name);
            Assert.True(definition.Enabled);
            Assert.Equal(Level.Debug, definition.MinLevel);
            Assert.IsType<TerminalRelayOptions>(definition.Options);
        }

        [Fact]
        public void RelayTerminal_ConfigureCallbackUpdatesTheDefinition()
        {
            var definition = Relay.Terminal(options =>
            {
                options.Name = "term";
                options.Enabled = false;
                options.MinLevel = Level.Error;
                options.QueueCapacity = 10;
                options.ExecutablePath = "C:\\tools\\glitchport-terminal.exe";
                options.Window = TerminalWindow.New;
                options.Format = TerminalFormat.Json;
                options.AppName = "My Game";
            });

            Assert.Equal("terminal", definition.Type);
            Assert.Equal("term", definition.Name);
            Assert.False(definition.Enabled);
            Assert.Equal(Level.Error, definition.MinLevel);

            var options = Assert.IsType<TerminalRelayOptions>(definition.Options);
            Assert.Equal("term", options.Name);
            Assert.False(options.Enabled);
            Assert.Equal(Level.Error, options.MinLevel);
            Assert.Equal(10, options.QueueCapacity);
            Assert.Equal("C:\\tools\\glitchport-terminal.exe", options.ExecutablePath);
            Assert.Equal(TerminalWindow.New, options.Window);
            Assert.Equal(TerminalFormat.Json, options.Format);
            Assert.Equal("My Game", options.AppName);
        }

        [Fact]
        public void TerminalRelay_WithoutOptions_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new TerminalRelay(null!));
        }

        [Fact]
        public async Task ExecutableDiscovery_TriesTheDocumentedLocationsInOrder()
        {
            var suffix = GetExecutableSuffix();
            var baseDirectory = AppContext.BaseDirectory;
            var baseCandidate = Path.Combine(baseDirectory, "glitchport-terminal" + suffix);
            var runtimeIdentifier = GetRuntimeIdentifier();
            var ridDirectory = runtimeIdentifier is null
                ? null
                : Path.Combine(baseDirectory, "runtimes", runtimeIdentifier);
            var nativeCandidate = ridDirectory is null
                ? null
                : Path.Combine(ridDirectory, "native", "glitchport-terminal" + suffix);

            var tempDirectory = CreateTempDirectory();
            var optionPath = Path.Combine(tempDirectory, "option-terminal" + suffix);
            var environmentPath = Path.Combine(tempDirectory, "environment-terminal" + suffix);

            using var baseBackup = new TemporaryRelocation(baseCandidate);
            using var nativeBackup = new TemporaryRelocation(nativeCandidate);
            using var environment = new TemporaryEnvironmentVariable(TerminalPathVariable, environmentPath);

            try
            {
                File.WriteAllText(optionPath, DummyExecutable);
                File.WriteAllText(environmentPath, DummyExecutable);
                File.WriteAllText(baseCandidate, DummyExecutable);

                if (nativeCandidate is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(nativeCandidate)!);
                    File.WriteAllText(nativeCandidate, DummyExecutable);
                }

                var optionResult = await RunRelayAsync(
                    new TerminalRelayOptions { ExecutablePath = optionPath });
                Assert.Equal(optionPath, optionResult.ExecutablePath);

                var environmentResult = await RunRelayAsync(new TerminalRelayOptions());
                Assert.Equal(environmentPath, environmentResult.ExecutablePath);

                environment.Set(null);

                var baseResult = await RunRelayAsync(new TerminalRelayOptions());
                Assert.Equal(baseCandidate, baseResult.ExecutablePath);

                if (nativeCandidate is null)
                {
                    return;
                }

                File.Delete(baseCandidate);

                var nativeResult = await RunRelayAsync(new TerminalRelayOptions());
                Assert.Equal(nativeCandidate, nativeResult.ExecutablePath);

                File.Delete(nativeCandidate);

                var missingResult = await RunRelayAsync(new TerminalRelayOptions());
                Assert.Null(missingResult.ExecutablePath);
                Assert.False(missingResult.Result.Succeeded);
                Assert.Contains("not found", missingResult.Result.LastError!, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                DeleteFile(optionPath);
                DeleteFile(environmentPath);
                DeleteFile(baseCandidate);

                if (nativeCandidate is not null)
                {
                    DeleteFile(nativeCandidate);
                    TryDeleteEmptyDirectory(Path.GetDirectoryName(nativeCandidate));
                }

                if (ridDirectory is not null)
                {
                    TryDeleteEmptyDirectory(ridDirectory);
                }

                TryDeleteDirectory(tempDirectory);
            }
        }

        [Fact]
        public async Task MissingExecutable_DisablesTheRelayWithoutThrowing()
        {
            var tempDirectory = CreateTempDirectory();
            var missingPath = Path.Combine(tempDirectory, "missing", "glitchport-terminal" + GetExecutableSuffix());
            var baseCandidate = Path.Combine(AppContext.BaseDirectory, "glitchport-terminal" + GetExecutableSuffix());
            var runtimeIdentifier = GetRuntimeIdentifier();
            var nativeCandidate = runtimeIdentifier is null
                ? null
                : Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
                    runtimeIdentifier,
                    "native",
                    "glitchport-terminal" + GetExecutableSuffix());

            using var baseBackup = new TemporaryRelocation(baseCandidate);
            using var nativeBackup = new TemporaryRelocation(nativeCandidate);
            using var environment = new TemporaryEnvironmentVariable(TerminalPathVariable, missingPath);

            try
            {
                using var relay = new TerminalRelay(new TerminalRelayOptions { ExecutablePath = missingPath });

                Assert.Null(relay.ProcessId);
                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(10));

                Assert.Null(relay.ProcessId);
                Assert.Equal(0L, result.Delivered);
                Assert.True(result.Failed >= 1);
                Assert.False(result.Succeeded);
                Assert.NotNull(result.LastError);
                Assert.Contains("not found", result.LastError!, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("glitchport-terminal", result.LastError!, StringComparison.Ordinal);

                // The relay is disabled now: further events are counted as failed
                // instead of retrying the executable lookup.
                Assert.False(relay.TryEnqueue(RelayTestData.CreateEvent()));
            }
            finally
            {
                TryDeleteDirectory(tempDirectory);
            }
        }

        [Fact]
        public async Task Relay_StartsTheChildLazilyAndDeliversQueuedEvents()
        {
            var shell = FindShell();
            var relay = new TerminalRelay(new TerminalRelayOptions
            {
                ExecutablePath = shell,
                AppName = "glitchport relay tests",
                Format = TerminalFormat.Compact,
                Window = TerminalWindow.Inherit,
            });

            int? childProcessId = null;

            try
            {
                // The executable is known, but the child only starts with the first
                // event.
                Assert.Null(relay.ProcessId);
                Assert.Equal(shell, relay.ExecutablePath);

                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent(message: "first")));
                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent(message: "second")));

                Assert.True(
                    await WaitUntilAsync(() => relay.ProcessId is not null, TimeSpan.FromSeconds(5)),
                    "the terminal child did not start within five seconds.");

                childProcessId = relay.ProcessId;
                Assert.NotNull(childProcessId);
                Assert.Equal(shell, relay.ExecutablePath);
                Assert.True(IsProcessAlive(childProcessId.Value));

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(15));

                Assert.Equal(2L, result.Delivered);
                Assert.Equal(0L, result.Failed);
                Assert.True(result.Succeeded, result.LastError);
            }
            finally
            {
                relay.Dispose();
                KillIfAlive(childProcessId ?? relay.ProcessId);
            }
        }

        [Fact]
        public async Task Dispose_ClosesStandardInputAndStopsTheChild()
        {
            var shell = FindShell();
            var relay = new TerminalRelay(new TerminalRelayOptions
            {
                ExecutablePath = shell,
                AppName = "dispose tests",
            });

            int? childProcessId = null;
            var stopped = false;

            try
            {
                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));

                Assert.True(
                    await WaitUntilAsync(() => relay.ProcessId is not null, TimeSpan.FromSeconds(5)),
                    "the terminal child did not start within five seconds.");

                childProcessId = relay.ProcessId;
                Assert.NotNull(childProcessId);

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(1L, result.Delivered);
            }
            finally
            {
                relay.Dispose();
                stopped = childProcessId is not null && !IsProcessAlive(childProcessId.Value);
                KillIfAlive(childProcessId ?? relay.ProcessId);
            }

            Assert.True(stopped, "Dispose left the terminal child running.");
        }

        [Fact]
        public async Task BrokenPipe_DisablesTheRelayWithoutStartingASecondChild()
        {
            var shell = FindShell();
            var relay = new TerminalRelay(new TerminalRelayOptions { ExecutablePath = shell });
            int? childProcessId = null;

            try
            {
                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent(message: "before the crash")));

                Assert.True(
                    await WaitUntilAsync(() => relay.ProcessId is not null, TimeSpan.FromSeconds(5)),
                    "the terminal child did not start within five seconds.");

                childProcessId = relay.ProcessId;
                Assert.NotNull(childProcessId);

                var first = await relay.FlushAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(1L, first.Delivered);

                using (var child = Process.GetProcessById(childProcessId.Value))
                {
                    child.Kill();
                    Assert.True(child.WaitForExit(5000), "the fake terminal child did not exit.");
                }

                // The next write hits a closed pipe: the event fails, the relay is
                // disabled and no replacement child is started.
                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent(message: "after the crash")));

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(15));

                Assert.Equal(1L, result.Delivered);
                Assert.True(result.Failed >= 1);
                Assert.False(result.Succeeded);
                Assert.NotNull(result.LastError);
                Assert.Equal(childProcessId, relay.ProcessId);
                Assert.False(relay.TryEnqueue(RelayTestData.CreateEvent()));
            }
            finally
            {
                relay.Dispose();
                KillIfAlive(childProcessId);
            }
        }

        [Fact]
        public async Task RelayTerminal_ThroughTheClient_StopsTheChildWhenTheClientIsDisposed()
        {
            var shell = FindShell();
            var definition = Relay.Terminal(options =>
            {
                options.ExecutablePath = shell;
                options.Name = "term";
                options.MinLevel = Level.Warning;
                options.Format = TerminalFormat.Json;
                options.AppName = "client tests";
            });

            var client = new GlitchPortClient(new GlitchPortOptions { Relays = { definition } });
            int? childProcessId = null;
            var stopped = false;

            try
            {
                var relay = Assert.IsType<TerminalRelay>(Assert.Single(client.Relays));
                Assert.Equal("terminal", relay.Type);
                Assert.Equal("term", relay.Name);
                Assert.Equal(Level.Warning, relay.MinLevel);
                Assert.Null(relay.ProcessId);

                await client.CaptureMessageAsync("terminal warning", Level.Warning);

                Assert.True(
                    await WaitUntilAsync(() => relay.ProcessId is not null, TimeSpan.FromSeconds(5)),
                    "the terminal child did not start within five seconds.");

                childProcessId = relay.ProcessId;
                Assert.NotNull(childProcessId);
                Assert.Equal(shell, relay.ExecutablePath);

                var report = await client.FlushAsync(TimeSpan.FromSeconds(15));
                Assert.True(report.AllSucceeded, report.Relays[0].LastError);
            }
            finally
            {
                client.Dispose();
                stopped = childProcessId is not null && !IsProcessAlive(childProcessId.Value);
                KillIfAlive(childProcessId);
            }

            Assert.True(stopped, "the client did not stop the terminal child.");
        }

        private static async Task<(string? ExecutablePath, RelayDeliveryResult Result)> RunRelayAsync(
            TerminalRelayOptions options)
        {
            using var relay = new TerminalRelay(options);

            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent(message: "discovery")));

            var result = await relay.FlushAsync(TimeSpan.FromSeconds(15));
            return (relay.ExecutablePath, result);
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
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

        private static bool IsProcessAlive(int processId)
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

        private static void KillIfAlive(int? processId)
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
                }
            }
            catch (Exception)
            {
                // Cleanup must never fail a test; the assertion above reports leaks.
            }
        }

        private static string FindShell()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
                if (!string.IsNullOrWhiteSpace(commandProcessor) && File.Exists(commandProcessor))
                {
                    return commandProcessor!;
                }

                var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var cmd = Path.Combine(systemDirectory, "cmd.exe");
                Assert.True(File.Exists(cmd), "cmd.exe was not found.");
                return cmd;
            }

            Assert.True(File.Exists("/bin/sh"), "/bin/sh was not found.");
            return "/bin/sh";
        }

        private static string GetExecutableSuffix()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;
        }

        private static string? GetRuntimeIdentifier()
        {
            string operatingSystem;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                operatingSystem = "win";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                operatingSystem = "linux";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                operatingSystem = "osx";
            }
            else
            {
                return null;
            }

            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    return operatingSystem + "-x64";
                case Architecture.X86:
                    return operatingSystem + "-x86";
                case Architecture.Arm:
                    return operatingSystem + "-arm";
                case Architecture.Arm64:
                    return operatingSystem + "-arm64";
                default:
                    return null;
            }
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

        private static void DeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Leftovers in the test output directory are harmless.
            }
        }

        private static void TryDeleteEmptyDirectory(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                {
                    Directory.Delete(path);
                }
            }
            catch (Exception)
            {
                // Leftovers in the test output directory are harmless.
            }
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
                // Leftovers in the temp directory are harmless.
            }
        }

        /// <summary>Restores an environment variable when the test finishes.</summary>
        private sealed class TemporaryEnvironmentVariable : IDisposable
        {
            private readonly string _name;
            private readonly string? _previousValue;

            public TemporaryEnvironmentVariable(string name, string? value)
            {
                _name = name;
                _previousValue = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            public void Set(string? value)
            {
                Environment.SetEnvironmentVariable(_name, value);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(_name, _previousValue);
            }
        }

        /// <summary>
        /// Moves a pre-existing executable out of the way while a test creates a
        /// fake one at the same location, then restores it.
        /// </summary>
        private sealed class TemporaryRelocation : IDisposable
        {
            private readonly string? _path;
            private readonly string? _backupPath;

            public TemporaryRelocation(string? path)
            {
                _path = path;

                try
                {
                    if (path is not null && File.Exists(path))
                    {
                        _backupPath = path + ".terminal-relay-test-" + Guid.NewGuid().ToString("N");
                        File.Move(path, _backupPath);
                    }
                }
                catch (Exception)
                {
                    // If the file cannot be moved the test still works; a real
                    // executable would only make the fake lookups less precise.
                }
            }

            public void Dispose()
            {
                if (_path is null || _backupPath is null)
                {
                    return;
                }

                try
                {
                    if (File.Exists(_backupPath))
                    {
                        if (File.Exists(_path))
                        {
                            File.Delete(_path);
                        }

                        File.Move(_backupPath, _path);
                    }
                }
                catch (Exception)
                {
                    // Best effort restoration.
                }
            }
        }
    }
}
