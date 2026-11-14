using System;
using System.Collections.Generic;

namespace GlitchPort
{
    /// <summary>Options that configure a <see cref="GlitchPortClient"/>.</summary>
    public sealed class GlitchPortOptions
    {
        /// <summary>The SDK name reported in the <c>sdk.name</c> payload field.</summary>
        public const string DefaultSdkName = "glitchport.dotnet";

        private const string DsnEnvironmentVariable = "GLITCHPORT_DSN";

        /// <summary>
        /// Gets or sets the DSN that identifies the relay and project. When set, the
        /// client adds an implicit HTTP relay named <c>http</c> in front of the
        /// entries in <see cref="Relays"/>.
        /// </summary>
        public Dsn? Dsn { get; set; }

        /// <summary>
        /// Gets the additional relays to create. Entries with
        /// <see cref="RelayDefinition.Enabled"/> set to <see langword="false"/> are
        /// skipped; the remaining entries are created in order.
        /// </summary>
        public IList<RelayDefinition> Relays { get; } = new List<RelayDefinition>();

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

        /// <summary>
        /// Creates options from the <c>GLITCHPORT_DSN</c> environment variable.
        /// </summary>
        /// <remarks>
        /// The environment variable holds a semicolon separated relay
        /// configuration string, for example
        /// <c>http://key@host:5080/1;terminal:?format=compact</c>. When the
        /// variable is missing or blank, the returned options contain no relays.
        /// <see cref="Dsn"/> is never set: every relay, including the HTTP one, is
        /// described by an explicit entry in <see cref="Relays"/>.
        /// </remarks>
        /// <returns>The options read from the environment.</returns>
        /// <exception cref="FormatException">A relay in <c>GLITCHPORT_DSN</c> is invalid.</exception>
        public static GlitchPortOptions FromEnvironment()
        {
            var options = new GlitchPortOptions();
            var value = Environment.GetEnvironmentVariable(DsnEnvironmentVariable);
            if (value is null || string.IsNullOrWhiteSpace(value))
            {
                return options;
            }

            var relays = RelayConfigParser.Parse(value);
            for (var i = 0; i < relays.Count; i++)
            {
                options.Relays.Add(relays[i]);
            }

            return options;
        }
    }
}
