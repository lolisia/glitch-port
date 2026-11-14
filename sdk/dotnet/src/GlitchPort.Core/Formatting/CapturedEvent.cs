using System;

namespace GlitchPort
{
    /// <summary>
    /// A single captured event plus its pre-serialized JSON body. Every relay
    /// forwards the same <see cref="Json"/> string.
    /// </summary>
    public sealed class CapturedEvent
    {
        /// <summary>Creates a captured event.</summary>
        /// <param name="eventId">The unique event id.</param>
        /// <param name="timestamp">The capture timestamp; stored in UTC.</param>
        /// <param name="level">The event severity.</param>
        /// <param name="message">The captured message.</param>
        /// <param name="sdkName">The SDK name reported to relays.</param>
        /// <param name="sdkVersion">The SDK version reported to relays.</param>
        /// <param name="json">The serialized event JSON shared by every relay.</param>
        public CapturedEvent(
            Guid eventId,
            DateTimeOffset timestamp,
            Level level,
            string message,
            string sdkName,
            string sdkVersion,
            string json)
        {
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (sdkName is null)
            {
                throw new ArgumentNullException(nameof(sdkName));
            }

            if (sdkVersion is null)
            {
                throw new ArgumentNullException(nameof(sdkVersion));
            }

            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            EventId = eventId;
            Timestamp = timestamp.ToUniversalTime();
            Level = level;
            Message = message;
            SdkName = sdkName;
            SdkVersion = sdkVersion;
            Json = json;
        }

        /// <summary>Gets the unique event id.</summary>
        public Guid EventId { get; }

        /// <summary>Gets the capture timestamp in UTC.</summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>Gets the event severity.</summary>
        public Level Level { get; }

        /// <summary>Gets the captured message.</summary>
        public string Message { get; }

        /// <summary>Gets the SDK name reported to relays.</summary>
        public string SdkName { get; }

        /// <summary>Gets the SDK version reported to relays.</summary>
        public string SdkVersion { get; }

        /// <summary>Gets the serialized event JSON that every relay forwards.</summary>
        public string Json { get; }
    }
}
