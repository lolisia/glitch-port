using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    /// <summary>
    /// Regression tests for the relay shutdown path. A renderer that never reads
    /// its standard input fills the pipe and blocks the relay worker inside the
    /// write; <see cref="TerminalRelay.Dispose"/> and
    /// <see cref="GlitchPortClient.Dispose"/> must still terminate the child,
    /// return within five seconds and never start a replacement child.
    /// </summary>
    public sealed class TerminalRelayShutdownTests
    {
        private const int PipeFillingEventCount = 8;
        private const int PipeFillingPayloadCharacters = 256 * 1024;

        [Fact]
        public async Task Dispose_WithAFullStandardInputPipe_IsBoundedAndKillsTheChild()
        {
            using var recording = TestTerminalChild.Create(TestTerminalChildMode.Ignore);

            var relay = new TerminalRelay(new TerminalRelayOptions
            {
                ExecutablePath = recording.ExecutablePath,
                AppName = "full pipe tests",
                QueueCapacity = 32,
            });

            int? childProcessId = null;

            try
            {
                childProcessId = await FillStandardInputPipeAsync(relay, recording);

                // The accessors are lock-free snapshots; they must return even
                // while the worker is blocked writing to the full pipe.
                var accessors = Task.Run(() => (relay.ProcessId, relay.ExecutablePath));
                Assert.True(
                    await TerminalTestProcess.CompletesWithinAsync(accessors, TimeSpan.FromSeconds(2)),
                    "ProcessId/ExecutablePath blocked behind a stdin write.");

                var snapshot = await accessors;
                Assert.Equal(childProcessId, snapshot.ProcessId);
                Assert.Equal(recording.ExecutablePath, snapshot.ExecutablePath);

                var stopwatch = Stopwatch.StartNew();
                var dispose = Task.Run(() => relay.Dispose());

                Assert.True(
                    await TerminalTestProcess.CompletesWithinAsync(dispose, TerminalTestProcess.DisposeBudget),
                    "TerminalRelay.Dispose did not return within five seconds.");
                await dispose;
                stopwatch.Stop();

                Assert.True(
                    stopwatch.Elapsed < TerminalTestProcess.DisposeBudget,
                    "TerminalRelay.Dispose took " + stopwatch.Elapsed + " while the stdin pipe was full.");

                Assert.True(
                    await TerminalTestProcess.WaitForExitAsync(childProcessId.Value, TimeSpan.FromSeconds(5)),
                    "the terminal child survived Dispose.");

                // Dispose is idempotent: a second call must not wait for the child
                // again and must not start anything.
                var secondDispose = Task.Run(() => relay.Dispose());
                Assert.True(
                    await TerminalTestProcess.CompletesWithinAsync(secondDispose, TimeSpan.FromSeconds(2)),
                    "the second TerminalRelay.Dispose call was not bounded.");
                await secondDispose;

                await Task.Delay(250);

                Assert.Single(recording.ReadStartedProcessIds());
                Assert.Equal(childProcessId, relay.ProcessId);
                Assert.Equal(recording.ExecutablePath, relay.ExecutablePath);
                Assert.False(relay.TryEnqueue(RelayTestData.CreateEvent()));
            }
            finally
            {
                relay.Dispose();
                TerminalTestProcess.KillIfAlive(childProcessId);
            }
        }

        [Fact]
        public async Task ClientDispose_WithAFullStandardInputPipe_IsBoundedAndKillsTheChild()
        {
            using var recording = TestTerminalChild.Create(TestTerminalChildMode.Ignore);

            var definition = Relay.Terminal(options =>
            {
                options.ExecutablePath = recording.ExecutablePath;
                options.AppName = "client full pipe tests";
                options.QueueCapacity = 32;
            });

            var client = new GlitchPortClient(new GlitchPortOptions { Relays = { definition } });
            var relay = Assert.IsType<TerminalRelay>(Assert.Single(client.Relays));
            int? childProcessId = null;

            try
            {
                for (var i = 0; i < PipeFillingEventCount; i++)
                {
                    await client.CaptureMessageAsync(CreateLargeMessage(i));
                }

                childProcessId = await ConfirmWorkerIsBlockedAsync(relay, recording);

                // GlitchPortClient.Dispose flushes for two seconds, then disposes
                // the relays. It must still finish well inside the five second
                // budget even though the relay worker cannot write.
                var stopwatch = Stopwatch.StartNew();
                var dispose = Task.Run(() => client.Dispose());

                Assert.True(
                    await TerminalTestProcess.CompletesWithinAsync(dispose, TerminalTestProcess.DisposeBudget),
                    "GlitchPortClient.Dispose did not return within five seconds.");
                await dispose;
                stopwatch.Stop();

                Assert.True(
                    stopwatch.Elapsed < TerminalTestProcess.DisposeBudget,
                    "GlitchPortClient.Dispose took " + stopwatch.Elapsed + " while the stdin pipe was full.");

                Assert.True(
                    await TerminalTestProcess.WaitForExitAsync(childProcessId.Value, TimeSpan.FromSeconds(5)),
                    "the terminal child survived GlitchPortClient.Dispose.");

                await Task.Delay(250);

                Assert.Single(recording.ReadStartedProcessIds());
                Assert.Equal(childProcessId, relay.ProcessId);
            }
            finally
            {
                client.Dispose();
                TerminalTestProcess.KillIfAlive(childProcessId ?? relay.ProcessId);
            }
        }

        [Fact]
        public async Task ManySmallEvents_DoNotMakeDisposeWaitForever()
        {
            using var recording = TestTerminalChild.Create(TestTerminalChildMode.Ignore);

            var relay = new TerminalRelay(new TerminalRelayOptions
            {
                ExecutablePath = recording.ExecutablePath,
                AppName = "many events",
                QueueCapacity = 32768,
            });

            int? childProcessId = null;

            try
            {
                // Enough lines that the pipe capacity is exceeded even with a
                // fairly large pipe buffer (more than a megabyte of protocol data).
                for (var i = 0; i < 20000; i++)
                {
                    Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent(message: "event " + i)));
                }

                childProcessId = await ConfirmWorkerIsBlockedAsync(relay, recording);

                var dispose = Task.Run(() => relay.Dispose());

                Assert.True(
                    await TerminalTestProcess.CompletesWithinAsync(dispose, TerminalTestProcess.DisposeBudget),
                    "TerminalRelay.Dispose did not return within five seconds.");
                await dispose;

                Assert.True(
                    await TerminalTestProcess.WaitForExitAsync(childProcessId.Value, TimeSpan.FromSeconds(5)),
                    "the terminal child survived Dispose.");
                Assert.Single(recording.ReadStartedProcessIds());
            }
            finally
            {
                relay.Dispose();
                TerminalTestProcess.KillIfAlive(childProcessId);
            }
        }

        /// <summary>
        /// Fills the stdin pipe with large events and returns the child pid after
        /// proving that the worker is stuck on the write.
        /// </summary>
        private static async Task<int> FillStandardInputPipeAsync(
            TerminalRelay relay,
            TestTerminalChildRecording recording)
        {
            for (var i = 0; i < PipeFillingEventCount; i++)
            {
                Assert.True(
                    relay.TryEnqueue(RelayTestData.CreateEvent(message: CreateLargeMessage(i))),
                    "the relay rejected large event " + i.ToString(CultureInfo.InvariantCulture) + ".");
            }

            return await ConfirmWorkerIsBlockedAsync(relay, recording);
        }

        /// <summary>
        /// Waits for the child, then uses a flush barrier with a short timeout:
        /// the barrier is queued behind the events and cannot be reached while
        /// the worker is blocked on the full stdin pipe.
        /// </summary>
        private static async Task<int> ConfirmWorkerIsBlockedAsync(
            TerminalRelay relay,
            TestTerminalChildRecording recording)
        {
            Assert.True(
                await TerminalTestProcess.WaitUntilAsync(
                    () => relay.ProcessId is not null,
                    TimeSpan.FromSeconds(10)),
                "the terminal child did not start within ten seconds.");

            Assert.True(
                await TestTerminalChild.WaitForStartAsync(recording, TimeSpan.FromSeconds(10)),
                "the fake terminal child did not record its start.");

            var flush = await relay.FlushAsync(TimeSpan.FromMilliseconds(1000));

            Assert.False(
                flush.Succeeded,
                "the relay worker was not blocked on the stdin pipe; the pipe-fill setup is inconclusive.");

            var processId = relay.ProcessId;
            Assert.NotNull(processId);
            return processId!.Value;
        }

        private static string CreateLargeMessage(int index)
        {
            return index.ToString(CultureInfo.InvariantCulture) + ":" + new string('x', PipeFillingPayloadCharacters);
        }
    }
}
