using System;
using System.Collections.Generic;

namespace GlitchPort
{
    /// <summary>Counters for one relay at the time of a flush.</summary>
    public sealed class RelayDeliveryResult
    {
        /// <summary>Creates a delivery result.</summary>
        /// <param name="name">The relay name.</param>
        /// <param name="type">The relay type.</param>
        /// <param name="delivered">The number of events delivered so far.</param>
        /// <param name="failed">The number of events that failed to deliver.</param>
        /// <param name="dropped">The number of events dropped because the queue was full.</param>
        /// <param name="lastError">The most recent error, when any.</param>
        public RelayDeliveryResult(
            string name,
            string type,
            long delivered,
            long failed,
            long dropped,
            string? lastError)
            : this(name, type, delivered, failed, dropped, 0, lastError)
        {
        }

        /// <summary>Creates a delivery result with the number of events still queued offline.</summary>
        /// <param name="name">The relay name.</param>
        /// <param name="type">The relay type.</param>
        /// <param name="delivered">The number of events delivered so far.</param>
        /// <param name="failed">The number of events that failed to deliver.</param>
        /// <param name="dropped">The number of events dropped because the queue was full.</param>
        /// <param name="queued">The number of events waiting in the relay's offline queue.</param>
        /// <param name="lastError">The most recent error, when any.</param>
        public RelayDeliveryResult(
            string name,
            string type,
            long delivered,
            long failed,
            long dropped,
            long queued,
            string? lastError)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Delivered = delivered;
            Failed = failed;
            Dropped = dropped;
            Queued = queued;
            LastError = lastError;
        }

        /// <summary>Gets the relay name.</summary>
        public string Name { get; }

        /// <summary>Gets the relay type.</summary>
        public string Type { get; }

        /// <summary>Gets the number of events the relay delivered successfully.</summary>
        public long Delivered { get; }

        /// <summary>Gets the number of events the relay failed to deliver.</summary>
        public long Failed { get; }

        /// <summary>Gets the number of events dropped because the queue was full.</summary>
        public long Dropped { get; }

        /// <summary>
        /// Gets the number of events waiting in the relay's on-disk offline
        /// queue at the time of the snapshot. <c>0</c> when no queue is configured.
        /// </summary>
        public long Queued { get; }

        /// <summary>Gets the most recent delivery error, when any.</summary>
        public string? LastError { get; }

        /// <summary>Gets a value indicating whether nothing failed and nothing was dropped.</summary>
        public bool Succeeded => Failed == 0 && Dropped == 0;
    }

    /// <summary>The per-relay results of a single <see cref="GlitchPortClient.FlushAsync"/> call.</summary>
    public sealed class DeliveryReport
    {
        /// <summary>Creates a report from the results of every relay.</summary>
        /// <param name="relays">The per-relay results.</param>
        public DeliveryReport(IReadOnlyList<RelayDeliveryResult> relays)
        {
            if (relays is null)
            {
                throw new ArgumentNullException(nameof(relays));
            }

            var copy = new RelayDeliveryResult[relays.Count];
            for (var i = 0; i < relays.Count; i++)
            {
                copy[i] = relays[i];
            }

            Relays = Array.AsReadOnly(copy);
        }

        /// <summary>Gets one result per relay, in relay order.</summary>
        public IReadOnlyList<RelayDeliveryResult> Relays { get; }

        /// <summary>
        /// Gets a value indicating whether every relay succeeded. An empty relay
        /// list counts as success.
        /// </summary>
        public bool AllSucceeded
        {
            get
            {
                for (var i = 0; i < Relays.Count; i++)
                {
                    if (!Relays[i].Succeeded)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
