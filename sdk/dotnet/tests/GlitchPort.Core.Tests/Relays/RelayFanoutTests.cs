using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class RelayFanoutTests
    {
        [Fact]
        public void Enqueue_SendsOnlyToRelaysWhoseMinLevelIsAtOrBelowTheEventLevel()
        {
            var debugRelay = new FakeRelay("debug", minLevel: Level.Debug);
            var warningRelay = new FakeRelay("warning", minLevel: Level.Warning);
            using var fanout = new RelayFanout(new IRelay[] { debugRelay, warningRelay });

            Assert.True(fanout.Enqueue(RelayTestData.CreateEvent(Level.Info)));

            Assert.Single(debugRelay.Events);
            Assert.Empty(warningRelay.Events);

            Assert.True(fanout.Enqueue(RelayTestData.CreateEvent(Level.Warning)));

            Assert.Equal(2, debugRelay.Events.Count);
            Assert.Single(warningRelay.Events);
        }

        [Fact]
        public void Enqueue_WhenNoRelayMatchesTheLevelOrAccepts_ReturnsFalse()
        {
            var highLevelRelay = new FakeRelay("high", minLevel: Level.Fatal);
            var rejectingRelay = new FakeRelay("rejecting") { Accept = false };
            using var fanout = new RelayFanout(new IRelay[] { highLevelRelay, rejectingRelay });

            Assert.False(fanout.Enqueue(RelayTestData.CreateEvent(Level.Info)));
            Assert.Empty(highLevelRelay.Events);
            Assert.Single(rejectingRelay.Events);
        }

        [Fact]
        public void Enqueue_WhenOneRelayThrows_StillDeliversToTheOthers()
        {
            var throwingRelay = new FakeRelay("throwing") { ThrowOnEnqueue = true };
            var healthyRelay = new FakeRelay("healthy");
            using var fanout = new RelayFanout(new IRelay[] { throwingRelay, healthyRelay });

            Assert.True(fanout.Enqueue(RelayTestData.CreateEvent()));
            Assert.Single(healthyRelay.Events);
        }

        [Fact]
        public void Enqueue_WhenOneRelayWorkerIsStalled_StillReachesTheOtherRelay()
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var stalled = new StallingRelay(started, release);
            var healthy = new FakeRelay("healthy");
            using var fanout = new RelayFanout(new IRelay[] { stalled, healthy });

            try
            {
                Assert.True(fanout.Enqueue(RelayTestData.CreateEvent()));
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

                // The stalled relay's worker is blocked inside Handle, but enqueueing
                // still returns immediately and the healthy relay receives the event.
                Assert.True(fanout.Enqueue(RelayTestData.CreateEvent()));
                Assert.Equal(2, healthy.Events.Count);
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public async Task FlushAsync_ReturnsOneResultPerRelay()
        {
            var first = new FakeRelay("first");
            var second = new FakeRelay("second");
            using var fanout = new RelayFanout(new IRelay[] { first, second });

            fanout.Enqueue(RelayTestData.CreateEvent());
            var report = await fanout.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, report.Relays.Count);
            Assert.Equal("first", report.Relays[0].Name);
            Assert.Equal("second", report.Relays[1].Name);
            Assert.True(report.AllSucceeded);
        }

        [Fact]
        public async Task FlushAsync_WhenRelayThrows_ReportsThatRelayAsFailed()
        {
            var throwingRelay = new FakeRelay("throwing") { ThrowOnFlush = true };
            var healthyRelay = new FakeRelay("healthy");
            using var fanout = new RelayFanout(new IRelay[] { throwingRelay, healthyRelay });

            var report = await fanout.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, report.Relays.Count);
            Assert.False(report.Relays[0].Succeeded);
            Assert.Equal(1, report.Relays[0].Failed);
            Assert.Contains("flush failure", report.Relays[0].LastError, StringComparison.Ordinal);
            Assert.True(report.Relays[1].Succeeded);
            Assert.False(report.AllSucceeded);
        }

        [Fact]
        public async Task FlushAsync_StartsEveryRelayBeforeWaitingForAnyOfThem()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = new FakeRelay("first")
            {
                FlushHandler = async _ =>
                {
                    firstStarted.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                    return new RelayDeliveryResult("first", "fake", 0, 0, 0, null);
                },
            };

            var second = new FakeRelay("second")
            {
                FlushHandler = async _ =>
                {
                    secondStarted.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                    return new RelayDeliveryResult("second", "fake", 0, 0, 0, null);
                },
            };

            using var fanout = new RelayFanout(new IRelay[] { first, second });
            var flush = fanout.FlushAsync(TimeSpan.FromSeconds(5));

            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

            release.TrySetResult(true);
            var report = await flush;

            Assert.True(report.AllSucceeded);
        }

        [Fact]
        public async Task FlushAsync_WhenARelayIgnoresTheTimeout_StillReturnsWithinTheTimeout()
        {
            var never = new TaskCompletionSource<RelayDeliveryResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var stalled = new FakeRelay("stalled") { FlushHandler = _ => never.Task };
            var healthy = new FakeRelay("healthy");
            using var fanout = new RelayFanout(new IRelay[] { stalled, healthy });

            var stopwatch = Stopwatch.StartNew();
            var report = await fanout.FlushAsync(TimeSpan.FromMilliseconds(150));
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                "FlushAsync blocked for " + stopwatch.Elapsed + ".");
            Assert.Equal(2, report.Relays.Count);
            Assert.False(report.Relays[0].Succeeded);
            Assert.StartsWith("flush timed out", report.Relays[0].LastError, StringComparison.Ordinal);
            Assert.True(report.Relays[1].Succeeded);
            Assert.False(report.AllSucceeded);

            never.TrySetResult(new RelayDeliveryResult("stalled", "fake", 0, 0, 0, null));
        }

        [Fact]
        public async Task FlushAsync_WithNoRelays_AllSucceededIsTrue()
        {
            using var fanout = new RelayFanout(Array.Empty<IRelay>());

            var report = await fanout.FlushAsync(TimeSpan.FromSeconds(1));

            Assert.Empty(report.Relays);
            Assert.True(report.AllSucceeded);
        }

        [Fact]
        public void Dispose_DisposesEveryRelay()
        {
            var first = new FakeRelay("first");
            var second = new FakeRelay("second");
            var fanout = new RelayFanout(new IRelay[] { first, second });

            fanout.Dispose();
            fanout.Dispose();

            Assert.True(first.Disposed);
            Assert.True(second.Disposed);
        }

        private sealed class StallingRelay : QueuedRelay
        {
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _release;

            public StallingRelay(ManualResetEventSlim started, ManualResetEventSlim release)
                : base("stalled", "fake", Level.Debug, queueCapacity: 10)
            {
                _started = started;
                _release = release;
            }

            protected override void Handle(CapturedEvent e)
            {
                _started.Set();
                _release.Wait();
                MarkDelivered();
            }
        }
    }
}
