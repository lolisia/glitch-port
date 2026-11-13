namespace GlitchPort
{
    /// <summary>Options that configure a <see cref="GlitchPortClient"/>.</summary>
    public sealed class GlitchPortOptions
    {
        /// <summary>The SDK name reported in the <c>sdk.name</c> payload field.</summary>
        public const string DefaultSdkName = "glitchport.dotnet";

        /// <summary>Gets or sets the DSN that identifies the relay and project. Required.</summary>
        public Dsn Dsn { get; set; } = null!;

        /// <summary>
        /// Gets or sets the SDK name reported to the relay. Defaults to
        /// <see cref="DefaultSdkName"/>.
        /// </summary>
        public string SdkName { get; set; } = DefaultSdkName;

        /// <summary>
        /// Gets or sets the SDK version reported to the relay. When <see langword="null"/>
        /// the client uses the informational version of the GlitchPort.Core assembly.
        /// </summary>
        public string? SdkVersion { get; set; }
    }
}
