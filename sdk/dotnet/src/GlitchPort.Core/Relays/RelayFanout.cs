using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort
{
    /// <summary>
    /// Fans events out to a set of independent relays and collects their delivery
    /// results. One slow or failing relay never blocks the caller or the other
    /// relays.
    /// </summary>
    public sealed class RelayFanout : IDisposable
    {
        private int _disposed;

        /// <summary>Creates a fan-out over the given relays.</summary>
        /// <param name="relays">The relays to fan out to. The list is copied.</param>
        public RelayFanout(IReadOnlyList<IRelay> relays)
        {
            if (relays is null)
            {
                throw new ArgumentNullException(nameof(relays));
            }

            var copy = new IRelay[relays.Count];
            for (var i = 0; i < relays.Count; i++)
            {
                copy[i] = relays[i]
                    ?? throw new ArgumentException("Relay entries must not be null.", nameof(relays));
            }

            Relays = Array.AsReadOnly(copy);
        }

        /// <summary>Gets the relays in fan-out order.</summary>
        public IReadOnlyList<IRelay> Relays { get; }

        /// <summary>
        /// Enqueues an event to every relay whose <see cref="IRelay.MinLevel"/> is
        /// at or below the event level.
        /// </summary>
        /// <param name="e">The event to enqueue.</param>
        /// <returns><see langword="true"/> when at least one relay accepted the event.</returns>
        public bool Enqueue(CapturedEvent e)
        {
            if (e is null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            var accepted = false;

            for (var i = 0; i < Relays.Count; i++)
            {
                var relay = Relays[i];
                if (relay.MinLevel > e.Level)
                {
                    continue;
                }

                try
                {
                    if (relay.TryEnqueue(e))
                    {
                        accepted = true;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // A relay disposed by someone else must not break capture.
                }
                catch (Exception)
                {
                    // Delivery failures are reported through FlushAsync; capture never throws.
                }
            }

            return accepted;
        }

        /// <summary>
        /// Flushes every relay concurrently and returns one result per relay.
        /// A relay whose flush throws is reported as failed instead of failing the
        /// whole report.
        /// </summary>
        /// <param name="timeout">The maximum time each relay may spend flushing.</param>
        /// <returns>The combined delivery report.</returns>
        public Task<DeliveryReport> FlushAsync(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The timeout must be non-negative or infinite.");
            }

            var relays = Relays;
            if (relays.Count == 0)
            {
                return Task.FromResult(new DeliveryReport(Array.Empty<RelayDeliveryResult>()));
            }

            var tasks = new Task<RelayDeliveryResult>[relays.Count];
            for (var i = 0; i < relays.Count; i++)
            {
                tasks[i] = FlushRelayAsync(relays[i], timeout);
            }

            return CompleteFlushAsync(tasks, relays, timeout);
        }

        /// <summary>Disposes every relay, ignoring errors from individual relays.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            for (var i = 0; i < Relays.Count; i++)
            {
                try
                {
                    Relays[i].Dispose();
                }
                catch (Exception)
                {
                    // Disposal is best effort; one relay must not leak the others.
                }
            }
        }

        private static async Task<DeliveryReport> CompleteFlushAsync(
            Task<RelayDeliveryResult>[] tasks,
            IReadOnlyList<IRelay> relays,
            TimeSpan timeout)
        {
            var all = Task.WhenAll(tasks);

            if (timeout == Timeout.InfiniteTimeSpan)
            {
                return new DeliveryReport(await all.ConfigureAwait(false));
            }

            using (var cancellation = new CancellationTokenSource())
            {
                var delay = Task.Delay(timeout, cancellation.Token);
                var winner = await Task.WhenAny(all, delay).ConfigureAwait(false);

                if (winner == all)
                {
                    cancellation.Cancel();
                    return new DeliveryReport(await all.ConfigureAwait(false));
                }
            }

            // A relay that ignores its timeout must not hang the caller: report the
            // relays that finished and mark the stragglers as failed.
            var results = new RelayDeliveryResult[tasks.Length];
            var error = "flush timed out after "
                + timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)
                + " ms.";

            for (var i = 0; i < tasks.Length; i++)
            {
                results[i] = tasks[i].Status == TaskStatus.RanToCompletion
                    ? tasks[i].Result
                    : new RelayDeliveryResult(relays[i].Name, relays[i].Type, 0, 1, 0, error);
            }

            return new DeliveryReport(results);
        }

        private static async Task<RelayDeliveryResult> FlushRelayAsync(IRelay relay, TimeSpan timeout)
        {
            try
            {
                var result = await relay.FlushAsync(timeout).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                return new RelayDeliveryResult(
                    relay.Name,
                    relay.Type,
                    0,
                    1,
                    0,
                    "flush failed: " + RelayErrorText.Describe(ex));
            }

            return new RelayDeliveryResult(relay.Name, relay.Type, 0, 1, 0, "flush returned no result");
        }
    }
}
