using System;
using System.Collections.Generic;

namespace GlitchPort
{
    /// <summary>Options that configure a <see cref="GlitchPortClient"/>.</summary>
    public sealed class GlitchPortOptions
    {
        /// <summary>The SDK name reported in the <c>sdk.name</c> payload field.</summary>
        public const string DefaultSdkName = "glitchport.dotnet";

        /// <summary>The platform reported when <see cref="Platform"/> is not set.</summary>
        public const string DefaultPlatform = "dotnet";

        private const string DsnEnvironmentVariable = "GLITCHPORT_DSN";
        private const string ReleaseEnvironmentVariable = "GLITCHPORT_RELEASE";
        private const string EnvironmentEnvironmentVariable = "GLITCHPORT_ENVIRONMENT";

        private double _sampleRate = 1d;

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
        /// Gets or sets the release identifier copied onto every event, for example
        /// <c>mygame@1.2.3</c>. Read from <c>GLITCHPORT_RELEASE</c> by
        /// <see cref="FromEnvironment"/>.
        /// </summary>
        public string? Release { get; set; }

        /// <summary>
        /// Gets or sets the deployment environment copied onto every event, for
        /// example <c>production</c>. Read from <c>GLITCHPORT_ENVIRONMENT</c> by
        /// <see cref="FromEnvironment"/>.
        /// </summary>
        public string? Environment { get; set; }

        /// <summary>
        /// Gets or sets the server or host name copied onto every event. Defaults
        /// to <see cref="System.Environment.MachineName"/>, or <see langword="null"/>
        /// when the runtime cannot provide it.
        /// </summary>
        public string? ServerName { get; set; } = GetDefaultServerName();

        /// <summary>
        /// Gets or sets the platform reported on every event. Defaults to
        /// <see cref="DefaultPlatform"/> (<c>dotnet</c>).
        /// </summary>
        public string Platform { get; set; } = DefaultPlatform;

        /// <summary>
        /// Gets or sets the fraction of events that is sent, between 0 and 1.
        /// Defaults to 1 (send everything).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is outside the inclusive range 0 to 1.
        /// </exception>
        public double SampleRate
        {
            get => _sampleRate;
            set
            {
                if (!(value >= 0d && value <= 1d))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        value,
                        "SampleRate must be between 0 and 1.");
                }

                _sampleRate = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of breadcrumbs kept for an event.
        /// Defaults to 100.
        /// </summary>
        public int MaxBreadcrumbs { get; set; } = 100;

        /// <summary>
        /// Gets or sets a callback that can inspect, modify or drop an event before
        /// it is sent. Returning <see langword="null"/> drops the event.
        /// </summary>
        public Func<GlitchPortEvent, GlitchPortEvent?>? BeforeSend { get; set; }

        /// <summary>
        /// Gets or sets the module prefixes that mark a stack frame as application
        /// code. When the list is empty (the default), <see cref="InAppExclude"/>
        /// decides instead.
        /// </summary>
        public IList<string> InAppInclude { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the module prefixes that mark a stack frame as engine or
        /// framework code. Defaults to <c>System.</c>, <c>Microsoft.</c>,
        /// <c>GlitchPort.</c>, <c>UnityEngine.</c>, <c>UnityEditor.</c> and
        /// <c>Unity.</c>.
        /// </summary>
        public IList<string> InAppExclude { get; set; } = new List<string>
        {
            "System.",
            "Microsoft.",
            "GlitchPort.",
            "UnityEngine.",
            "UnityEditor.",
            "Unity.",
        };

        /// <summary>
        /// Gets or sets a value indicating whether personally identifiable
        /// information, such as the user name or IP address, may be collected and
        /// sent. Defaults to <see langword="false"/>.
        /// </summary>
        public bool SendDefaultPii { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether unhandled exceptions are
        /// captured automatically. Defaults to <see langword="false"/>.
        /// </summary>
        public bool CaptureUnhandledExceptions { get; set; }

        /// <summary>
        /// Creates options from the <c>GLITCHPORT_DSN</c>,
        /// <c>GLITCHPORT_RELEASE</c> and <c>GLITCHPORT_ENVIRONMENT</c> environment
        /// variables.
        /// </summary>
        /// <remarks>
        /// The DSN variable holds a semicolon separated relay configuration string,
        /// for example <c>http://key@host:5080/1;terminal:?format=compact</c>. When
        /// it is missing or blank, the returned options contain no relays.
        /// <see cref="Release"/> and <see cref="Environment"/> are read regardless
        /// of whether a DSN is configured; blank values leave the corresponding
        /// option <see langword="null"/>. <see cref="Dsn"/> is never set: every
        /// relay, including the HTTP one, is described by an explicit entry in
        /// <see cref="Relays"/>.
        /// </remarks>
        /// <returns>The options read from the environment.</returns>
        /// <exception cref="FormatException">A relay in <c>GLITCHPORT_DSN</c> is invalid.</exception>
        public static GlitchPortOptions FromEnvironment()
        {
            var options = new GlitchPortOptions();

            var release = System.Environment.GetEnvironmentVariable(ReleaseEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(release))
            {
                options.Release = release;
            }

            var environment = System.Environment.GetEnvironmentVariable(EnvironmentEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environment))
            {
                options.Environment = environment;
            }

            var value = System.Environment.GetEnvironmentVariable(DsnEnvironmentVariable);
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

        private static string? GetDefaultServerName()
        {
            try
            {
                return System.Environment.MachineName;
            }
            catch (Exception)
            {
                // Some restricted runtimes do not expose a machine name; the event
                // simply omits server_name in that case.
                return null;
            }
        }
    }
}
