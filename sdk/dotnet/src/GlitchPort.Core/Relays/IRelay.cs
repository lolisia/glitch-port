using System;
using System.Threading.Tasks;

namespace GlitchPort
{
    /// <summary>
    /// A destination that accepts captured events and delivers them outside the
    /// caller's thread. Implementations must never block the caller of
    /// <see cref="TryEnqueue"/> and must never let a delivery failure escape
    /// <see cref="FlushAsync"/>.
    /// </summary>
    public interface IRelay : IDisposable
    {
        /// <summary>Gets the configured relay name, for example <c>http</c>.</summary>
        string Name { get; }

        /// <summary>Gets the relay type, for example <c>http</c>.</summary>
        string Type { get; }

        /// <summary>Gets the minimum level an event must have to reach this relay.</summary>
        Level MinLevel { get; }

        /// <summary>
        /// Tries to queue an event without blocking.
        /// </summary>
        /// <param name="e">The event to queue.</param>
        /// <returns>
        /// <see langword="true"/> when the relay accepted the event; otherwise
        /// <see langword="false"/> (the relay is disabled or its queue is full).
        /// </returns>
        bool TryEnqueue(CapturedEvent e);

        /// <summary>
        /// Flushes everything queued before this call and returns a snapshot of the
        /// relay counters. The returned task must complete within
        /// <paramref name="timeout"/>.
        /// </summary>
        /// <param name="timeout">The maximum time to spend draining the queue.</param>
        /// <returns>The delivery result of this relay.</returns>
        Task<RelayDeliveryResult> FlushAsync(TimeSpan timeout);
    }
}
