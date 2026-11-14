using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GlitchPort.Tests
{
    public sealed class QueuedRelayTests
    {
        [Fact]
        public async Task FlushAsync_DeliversEveryQueuedEventAndCallsFlushBuffer()
        {
            using var relay = new TestRelay();

            for (var i = 0; i < 5; i++)
            {
                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
            }

            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(5, result.Delivered);
            Assert.Equal(0, result.Failed);
            Assert.Equal(0, result.Dropped);
            Assert.True(result.Succeeded);
            Assert.Equal(5, relay.Handled.Count);
            Assert.True(relay.FlushBufferCalls > 0);
        }

        [Fact]
        public async Task TryEnqueue_WhenQueueIsFull_CountsDropped()
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var relay = new TestRelay(
                queueCapacity: 2,
                onHandle: _ =>
                {
                    started.Set();
                    release.Wait();
                });

            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));

            // The worker is inside Handle, so the queue itself is empty; fill it to
            // capacity and prove the next event is dropped instead of blocking.
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
            Assert.False(relay.TryEnqueue(RelayTestData.CreateEvent()));

            release.Set();

            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, result.Delivered);
            Assert.Equal(0, result.Failed);
            Assert.Equal(1, result.Dropped);
            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task TryEnqueue_WhenRelayIsDisabled_CountsFailed()
        {
            using var relay = new TestRelay();
            relay.DisableRelay("disk full");

            Assert.False(relay.TryEnqueue(RelayTestData.CreateEvent()));

            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, result.Delivered);
            Assert.Equal(1, result.Failed);
            Assert.False(result.Succeeded);
            Assert.Contains("disk full", result.LastError, StringComparison.Ordinal);
        }

        [Fact]
        public async Task FlushAsync_WhenTheWorkerIsStuck_ReturnsATimeoutResult()
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var relay = new TestRelay(
                onHandle: _ =>
                {
                    started.Set();
                    release.Wait();
                });

            try
            {
                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

                var result = await relay.FlushAsync(TimeSpan.FromMilliseconds(100));

                Assert.False(result.Succeeded);
                Assert.True(result.Failed >= 1);
                Assert.StartsWith("flush timed out", result.LastError, StringComparison.Ordinal);
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public async Task Handle_WhenItThrows_CountsFailedAndKeepsTheWorkerAlive()
        {
            var attempts = 0;
            using var relay = new TestRelay(
                onHandle: _ =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        throw new InvalidOperationException("boom");
                    }
                });

            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));

            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, result.Delivered);
            Assert.Equal(1, result.Failed);
            Assert.False(result.Succeeded);
            Assert.Contains("boom", result.LastError, StringComparison.Ordinal);
            Assert.Equal(2, relay.Handled.Count);
        }

        [Fact]
        public async Task FlushBuffer_WhenItThrows_CountsFailed()
        {
            using var relay = new TestRelay { ThrowOnFlushBuffer = true };

            Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));

            var result = await relay.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, result.Delivered);
            Assert.Equal(1, result.Failed);
            Assert.False(result.Succeeded);
            Assert.Contains("flush buffer failure", result.LastError, StringComparison.Ordinal);
        }

        [Fact]
        public void Dispose_IsIdempotentAndReturnsQuicklyWhenTheWorkerIsStuck()
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var relay = new TestRelay(
                onHandle: _ =>
                {
                    started.Set();
                    release.Wait();
                });

            try
            {
                Assert.True(relay.TryEnqueue(RelayTestData.CreateEvent()));
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

                var stopwatch = Stopwatch.StartNew();
                relay.Dispose();
                relay.Dispose();
                stopwatch.Stop();

                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                    "Dispose blocked for " + stopwatch.Elapsed + ".");
                Assert.False(relay.TryEnqueue(RelayTestData.CreateEvent()));
            }
            finally
            {
                release.Set();
            }
        }

        private sealed class TestRelay : QueuedRelay
        {
            private readonly object _handledGate = new object();
            private readonly List<CapturedEvent> _handled = new List<CapturedEvent>();
            private readonly Action<CapturedEvent>? _onHandle;
            private int _flushBufferCalls;

            public TestRelay(
                string name = "test",
                Level minLevel = Level.Debug,
                int queueCapacity = 10,
                Action<CapturedEvent>? onHandle = null)
                : base(name, "test", minLevel, queueCapacity)
            {
                _onHandle = onHandle;
            }

            public bool ThrowOnFlushBuffer { get; set; }

            public int FlushBufferCalls => Volatile.Read(ref _flushBufferCalls);

            public IReadOnlyList<CapturedEvent> Handled
            {
                get
                {
                    lock (_handledGate)
                    {
                        return _handled.ToArray();
                    }
                }
            }

            public void DisableRelay(string error)
            {
                Disable(error);
            }

            protected override void Handle(CapturedEvent e)
            {
                lock (_handledGate)
                {
                    _handled.Add(e);
                }

                _onHandle?.Invoke(e);
                MarkDelivered();
            }

            protected override void FlushBuffer()
            {
                Interlocked.Increment(ref _flushBufferCalls);

                if (ThrowOnFlushBuffer)
                {
                    throw new InvalidOperationException("flush buffer failure");
                }
            }
        }
    }
}
