using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    /// <summary>
    /// Asserts the exact wire protocol and command line the relay produces when
    /// it starts a renderer. The child is a test-owned .NET executable, so the
    /// assertions hold on Windows and Linux without <c>cmd.exe</c> or
    /// <c>/bin/sh</c>.
    /// </summary>
    public sealed class TerminalRelayWireTests
    {
        [Fact]
        public async Task Relay_StartsWithTheConfiguredArgumentsAndWritesTheProtocolLines()
        {
            using var recording = TestTerminalChild.Create();
            const string appName = "wire 테스트";

            var relay = new TerminalRelay(new TerminalRelayOptions
            {
                ExecutablePath = recording.ExecutablePath,
                AppName = appName,
                Format = TerminalFormat.Json,
                Window = TerminalWindow.New,
            });

            int? childProcessId = null;

            try
            {
                var first = RelayTestData.CreateEvent(message: "첫 번째 이벤트");
                var second = RelayTestData.CreateEvent(message: "second event");

                Assert.True(relay.TryEnqueue(first));
                Assert.True(relay.TryEnqueue(second));

                Assert.True(
                    await TerminalTestProcess.WaitUntilAsync(
                        () => relay.ProcessId is not null,
                        TimeSpan.FromSeconds(5)),
                    "the terminal child did not start within five seconds.");

                childProcessId = relay.ProcessId;
                Assert.NotNull(childProcessId);

                // The parent pid is the pid of this test process, which is what
                // TerminalRelay resolved when it was constructed.
                var parentProcessId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
                var helloLine = "{\"type\":\"hello\",\"protocol\":1,\"parent_pid\":"
                    + parentProcessId
                    + ",\"app\":\"wire 테스트\"}";
                var firstLine = BuildEventLine(first.Json);
                var secondLine = BuildEventLine(second.Json);
                var expectedWireBytes = Encoding.UTF8.GetBytes(
                    helloLine + "\n" + firstLine + "\n" + secondLine + "\n");

                Assert.True(
                    await TerminalTestProcess.WaitUntilAsync(
                        () => recording.ReadStandardInputBytes().Length >= expectedWireBytes.Length,
                        TimeSpan.FromSeconds(10)),
                    "the fake terminal child did not receive the complete protocol.");

                // Exact command line (after .NET split the Arguments string into
                // argv, which is what the child records).
                Assert.Equal(
                    new[]
                    {
                        "--parent-pid",
                        parentProcessId,
                        "--format",
                        "json",
                        "--window",
                        "new",
                        "--app",
                        appName,
                    },
                    recording.ReadArguments());

                // Exact UTF-8 bytes: no BOM, LF line endings, hello first and one
                // event line per captured event with the untouched payload JSON.
                Assert.Equal(expectedWireBytes, recording.ReadStandardInputBytes());
                Assert.Equal((byte)'{', recording.ReadStandardInputBytes()[0]);
                Assert.Equal(
                    new[] { helloLine, firstLine, secondLine },
                    recording.ReadStandardInputLines());

                var result = await relay.FlushAsync(TimeSpan.FromSeconds(15));

                Assert.Equal(2L, result.Delivered);
                Assert.Equal(0L, result.Failed);
                Assert.True(result.Succeeded, result.LastError);
                Assert.Single(recording.ReadStartedProcessIds());
            }
            finally
            {
                relay.Dispose();
                TerminalTestProcess.KillIfAlive(childProcessId ?? relay.ProcessId);
            }
        }

        [Fact]
        public async Task Relay_UsesTheCompactAndInheritArgumentNames()
        {
            using var recording = TestTerminalChild.Create();

            var relay = new TerminalRelay(new TerminalRelayOptions
            {
                ExecutablePath = recording.ExecutablePath,
                AppName = "short names",
                Format = TerminalFormat.Compact,
                Window = TerminalWindow.Inherit,
            });

            int? childProcessId = null;

            try
            {
                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));

                Assert.True(
                    await TerminalTestProcess.WaitUntilAsync(
                        () => relay.ProcessId is not null,
                        TimeSpan.FromSeconds(5)),
                    "the terminal child did not start within five seconds.");

                childProcessId = relay.ProcessId;

                Assert.True(
                    await TestTerminalChild.WaitForStartAsync(recording, TimeSpan.FromSeconds(5)),
                    "the fake terminal child did not record its start.");

                Assert.Equal(
                    new[]
                    {
                        "--parent-pid",
                        Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                        "--format",
                        "compact",
                        "--window",
                        "inherit",
                        "--app",
                        "short names",
                    },
                    recording.ReadArguments());
            }
            finally
            {
                relay.Dispose();
                TerminalTestProcess.KillIfAlive(childProcessId ?? relay.ProcessId);
            }
        }

        private static string BuildEventLine(string payloadJson)
        {
            return "{\"type\":\"event\",\"payload\":" + payloadJson + "}";
        }
    }
}
