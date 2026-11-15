using System;

namespace GlitchPort
{
    /// <summary>
    /// Options for the HTTP relay's on-disk offline queue. The queue keeps
    /// serialized events while the relay endpoint is unreachable and retries
    /// them later.
    /// </summary>
    /// <remarks>
    /// The queue is disabled when <see cref="Directory"/> is <see langword="null"/>
    /// or blank, which is the default. <see cref="MaxEvents"/>, <see cref="MaxAgeHours"/>
    /// and <see cref="RetryIntervalSeconds"/> are validated when the queue (or
    /// the relay that owns it) is created.
    /// </remarks>
    public sealed class HttpOfflineQueueOptions
    {
        /// <summary>
        /// Gets or sets the directory that holds queued events.
        /// <see langword="null"/> or whitespace disables the queue. Defaults to
        /// <see langword="null"/>.
        /// </summary>
        public string? Directory { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of queued events. The oldest events
        /// are dropped first when the limit is exceeded. Defaults to 500.
        /// </summary>
        public int MaxEvents { get; set; } = 500;

        /// <summary>
        /// Gets or sets the maximum age, in hours, of a queued event. Older
        /// events are deleted. <c>0</c> disables the age limit. Defaults to 72.
        /// </summary>
        public int MaxAgeHours { get; set; } = 72;

        /// <summary>
        /// Gets or sets the interval, in seconds, between retry attempts for
        /// queued events. Defaults to 30.
        /// </summary>
        public int RetryIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Validates the numeric limits. Called when the queue or relay is created.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A limit is outside its supported range.</exception>
        internal void Validate()
        {
            if (MaxEvents < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxEvents),
                    MaxEvents,
                    "MaxEvents must be at least 1.");
            }

            if (MaxAgeHours < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxAgeHours),
                    MaxAgeHours,
                    "MaxAgeHours must not be negative.");
            }

            if (RetryIntervalSeconds < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RetryIntervalSeconds),
                    RetryIntervalSeconds,
                    "RetryIntervalSeconds must be at least 1.");
            }
        }
    }
}
