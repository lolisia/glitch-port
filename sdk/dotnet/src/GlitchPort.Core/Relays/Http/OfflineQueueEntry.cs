using System;

namespace GlitchPort
{
    /// <summary>One serialized event waiting in an <see cref="OfflineQueue"/>.</summary>
    internal sealed class OfflineQueueEntry
    {
        /// <summary>Creates a queue entry.</summary>
        /// <param name="path">The absolute path of the event file.</param>
        /// <param name="eventId">The event id embedded in the file name.</param>
        /// <param name="timestamp">The UTC event timestamp embedded in the file name.</param>
        /// <param name="json">The exact serialized event JSON read from the file.</param>
        public OfflineQueueEntry(string path, Guid eventId, DateTimeOffset timestamp, string json)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            EventId = eventId;
            Timestamp = timestamp;
            Json = json ?? throw new ArgumentNullException(nameof(json));
        }

        /// <summary>Gets the absolute path of the event file.</summary>
        public string Path { get; }

        /// <summary>Gets the event id embedded in the file name.</summary>
        public Guid EventId { get; }

        /// <summary>Gets the UTC event timestamp embedded in the file name.</summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>Gets the exact serialized event JSON read from the file.</summary>
        public string Json { get; }
    }
}
