using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort
{
    /// <summary>
    /// Base class for relays that hand events to one dedicated background worker
    /// through a bounded FIFO queue. Enqueueing never blocks; delivery, retries
    /// and error handling all happen on the worker thread.
    /// </summary>
    public abstract class QueuedRelay : IRelay
    {
        private const int WorkerShutdownTimeoutMilliseconds = 900;

        private readonly object _gate = new object();
        private readonly Queue<WorkItem> _queue;
        private readonly int _queueCapacity;

        private Thread? _worker;
        private long _delivered;
        private long _failed;
        private long _dropped;
        private long _pendingEvents;
        private int _queuedEvents;
        private string? _lastError;
        private bool _disabled;
        private bool _disposed;
        private bool _stopRequested;

        /// <summary>Creates a queued relay. The worker thread starts on first use.</summary>
        /// <param name="name">The relay name.</param>
        /// <param name="type">The relay type.</param>
        /// <param name="minLevel">The minimum event level the relay receives.</param>
        /// <param name="queueCapacity">The maximum number of queued events.</param>
        protected QueuedRelay(string name, string type, Level minLevel, int queueCapacity)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (queueCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(queueCapacity),
                    queueCapacity,
                    "The queue capacity must be positive.");
            }

            Name = name;
            Type = type;
            MinLevel = minLevel;
            _queueCapacity = queueCapacity;
            _queue = new Queue<WorkItem>(Math.Min(queueCapacity, 128));
        }

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public string Type { get; }

        /// <inheritdoc/>
        public Level MinLevel { get; }

        /// <summary>
        /// Gets a value indicating whether the relay stopped accepting new events
        /// (for example after an unrecoverable write error).
        /// </summary>
        protected bool IsDisabled => Volatile.Read(ref _disabled);

        /// <inheritdoc/>
        public bool TryEnqueue(CapturedEvent e)
        {
            if (e is null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            lock (_gate)
            {
                if (_disposed || IsDisabled)
                {
                    Interlocked.Increment(ref _failed);
                    return false;
                }

                if (_queuedEvents >= _queueCapacity)
                {
                    Interlocked.Increment(ref _dropped);
                    return false;
                }

                EnsureWorkerStartedLocked();
                _queue.Enqueue(new WorkItem(e, null));
                _queuedEvents++;
                _pendingEvents++;
                Monitor.PulseAll(_gate);
                return true;
            }
        }

        /// <inheritdoc/>
        public async Task<RelayDeliveryResult> FlushAsync(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The timeout must be non-negative or infinite.");
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var barrier = new WorkItem(null, completion);

            lock (_gate)
            {
                if (_disposed)
                {
                    return CreateResult();
                }

                // Barriers bypass the event capacity: they only mark a position in
                // the FIFO and are always consumed by the worker.
                EnsureWorkerStartedLocked();
                _queue.Enqueue(barrier);
                Monitor.PulseAll(_gate);
            }

            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await completion.Task.ConfigureAwait(false);
                return CreateResult();
            }

            if (!await CompletesWithinAsync(completion.Task, timeout).ConfigureAwait(false))
            {
                return CreateTimeoutResult(timeout);
            }

            return CreateResult();
        }

        /// <summary>
        /// Releases the relay. The worker drains the events already accepted, calls
        /// <see cref="FlushBuffer"/> and exits; the method never blocks for more
        /// than roughly one second because the worker is a background thread.
        /// </summary>
        public void Dispose()
        {
            Thread? worker;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopRequested = true;
                worker = _worker;
                Monitor.PulseAll(_gate);
            }

            try
            {
                // A hung subclass must not hang the caller (or process shutdown):
                // the background thread keeps draining on its own and exits later.
                worker?.Join(WorkerShutdownTimeoutMilliseconds);
            }
            catch (ThreadStateException)
            {
                // Disposing from the worker thread itself; the loop exits on its own.
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>Processes one event on the worker thread.</summary>
        /// <param name="e">The event to deliver.</param>
        protected abstract void Handle(CapturedEvent e);

        /// <summary>Flushes any internal buffer on the worker thread. Defaults to a no-op.</summary>
        protected virtual void FlushBuffer()
        {
        }

        /// <summary>Records <paramref name="count"/> successfully delivered events.</summary>
        /// <param name="count">The number of events; defaults to one.</param>
        protected void MarkDelivered(long count = 1)
        {
            Interlocked.Add(ref _delivered, count);
        }

        /// <summary>Records <paramref name="count"/> failed events and remembers the error.</summary>
        /// <param name="error">A short error description.</param>
        /// <param name="count">The number of events; defaults to one.</param>
        protected void MarkFailed(string error, long count = 1)
        {
            Interlocked.Add(ref _failed, count);
            _lastError = RelayErrorText.Sanitize(error);
        }

        /// <summary>Stops accepting new events and records the reason.</summary>
        /// <param name="error">A short error description.</param>
        protected void Disable(string error)
        {
            _lastError = RelayErrorText.Sanitize(error);
            Volatile.Write(ref _disabled, true);
        }

        private void EnsureWorkerStartedLocked()
        {
            if (_worker is not null)
            {
                return;
            }

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "glitchport relay " + Name,
            };
            _worker.Start();
        }

        private void WorkerLoop()
        {
            while (true)
            {
                WorkItem item;

                lock (_gate)
                {
                    while (_queue.Count == 0 && !_stopRequested)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_queue.Count == 0)
                    {
                        break;
                    }

                    item = _queue.Dequeue();

                    if (item.Event is not null)
                    {
                        _queuedEvents--;
                    }
                }

                if (item.Event is not null)
                {
                    ProcessEvent(item.Event);
                }
                else
                {
                    ProcessBarrier(item.Completion);
                }
            }

            try
            {
                FlushBuffer();
            }
            catch (Exception ex)
            {
                MarkFailed(RelayErrorText.Describe(ex));
            }
        }

        private void ProcessEvent(CapturedEvent e)
        {
            try
            {
                Handle(e);
            }
            catch (Exception ex)
            {
                // A failing delivery must never kill the worker thread; later events
                // still get their chance and the failure is visible in FlushAsync.
                MarkFailed(RelayErrorText.Describe(ex));
            }
            finally
            {
                lock (_gate)
                {
                    _pendingEvents--;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private void ProcessBarrier(TaskCompletionSource<bool>? completion)
        {
            try
            {
                FlushBuffer();
            }
            catch (Exception ex)
            {
                MarkFailed(RelayErrorText.Describe(ex));
            }

            completion?.TrySetResult(true);
        }

        private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
        {
            if (task.IsCompleted)
            {
                return true;
            }

            using (var cancellation = new CancellationTokenSource())
            {
                var delay = Task.Delay(timeout, cancellation.Token);
                var winner = await Task.WhenAny(task, delay).ConfigureAwait(false);

                if (winner == task)
                {
                    cancellation.Cancel();
                    return true;
                }

                return false;
            }
        }

        private RelayDeliveryResult CreateResult()
        {
            return new RelayDeliveryResult(
                Name,
                Type,
                Interlocked.Read(ref _delivered),
                Interlocked.Read(ref _failed),
                Interlocked.Read(ref _dropped),
                _lastError);
        }

        private RelayDeliveryResult CreateTimeoutResult(TimeSpan timeout)
        {
            long pending;

            lock (_gate)
            {
                pending = _pendingEvents;
            }

            var stillQueued = Math.Max(1, pending);
            var error = "flush timed out after "
                + timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)
                + " ms with "
                + stillQueued.ToString(CultureInfo.InvariantCulture)
                + " event(s) still queued.";

            return new RelayDeliveryResult(
                Name,
                Type,
                Interlocked.Read(ref _delivered),
                Interlocked.Read(ref _failed) + stillQueued,
                Interlocked.Read(ref _dropped),
                error);
        }

        private sealed class WorkItem
        {
            public WorkItem(CapturedEvent? e, TaskCompletionSource<bool>? completion)
            {
                Event = e;
                Completion = completion;
            }

            public CapturedEvent? Event { get; }

            public TaskCompletionSource<bool>? Completion { get; }
        }
    }

    /// <summary>Helpers for turning exceptions and response bodies into short, single-line errors.</summary>
    internal static class RelayErrorText
    {
        private const int MaxLength = 300;

        public static string Describe(Exception exception)
        {
            if (exception is null)
            {
                return "Unknown relay error.";
            }

            var message = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.GetType().Name + ": " + exception.Message;

            return Sanitize(message);
        }

        public static string Sanitize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var singleLine = value!
                .Replace("\r\n", " ")
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            return singleLine.Length <= MaxLength
                ? singleLine
                : singleLine.Substring(0, MaxLength) + "...";
        }
    }
}
