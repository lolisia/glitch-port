namespace GlitchPort
{
    /// <summary>Options shared by every relay type.</summary>
    public abstract class RelayOptions
    {
        /// <summary>
        /// Gets or sets the relay name. When <see langword="null"/> the relay uses
        /// its type name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets a value indicating whether the relay is registered. Defaults to <see langword="true"/>.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the minimum event level the relay receives. Defaults to <see cref="Level.Debug"/>.</summary>
        public Level MinLevel { get; set; } = Level.Debug;

        /// <summary>Gets or sets the maximum number of queued events. Defaults to 1000.</summary>
        public int QueueCapacity { get; set; } = 1000;
    }

    /// <summary>Options for the HTTP relay.</summary>
    public sealed class HttpRelayOptions : RelayOptions
    {
    }
}
