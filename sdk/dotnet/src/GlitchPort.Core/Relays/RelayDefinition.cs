using System;
using System.Net.Http;

namespace GlitchPort
{
    /// <summary>
    /// Describes a relay to create when a <see cref="GlitchPortClient"/> is
    /// constructed. A definition owns the relay's options and knows how to build
    /// the actual <see cref="IRelay"/> instance.
    /// </summary>
    public abstract class RelayDefinition
    {
        /// <summary>Gets the relay type, for example <c>http</c>.</summary>
        public abstract string Type { get; }

        /// <summary>Gets the options shared by every relay type.</summary>
        public abstract RelayOptions Options { get; }

        /// <summary>
        /// Gets or sets the relay name. Defaults to <see cref="Type"/> when no name
        /// was configured.
        /// </summary>
        public string Name
        {
            get => Options.Name ?? Type;
            set => Options.Name = value;
        }

        /// <summary>Gets or sets a value indicating whether the relay is created.</summary>
        public bool Enabled
        {
            get => Options.Enabled;
            set => Options.Enabled = value;
        }

        /// <summary>Gets or sets the minimum event level the relay receives.</summary>
        public Level MinLevel
        {
            get => Options.MinLevel;
            set => Options.MinLevel = value;
        }

        internal abstract IRelay CreateRelay(RelayCreationContext context);
    }

    /// <summary>State shared while relay instances are created for one client.</summary>
    internal sealed class RelayCreationContext
    {
        public RelayCreationContext(HttpClient? httpClient, string sdkName, string sdkVersion)
        {
            HttpClient = httpClient;
            SdkName = sdkName ?? throw new ArgumentNullException(nameof(sdkName));
            SdkVersion = sdkVersion ?? throw new ArgumentNullException(nameof(sdkVersion));
        }

        public HttpClient? HttpClient;

        public string SdkName;

        public string SdkVersion;
    }

    /// <summary>A relay definition that posts events to a glitch-port relay over HTTP.</summary>
    public sealed class HttpRelayDefinition : RelayDefinition
    {
        private const string TypeName = "http";

        private readonly Dsn _dsn;
        private readonly HttpRelayOptions _options;

        /// <summary>Creates an HTTP relay definition from a parsed DSN.</summary>
        /// <param name="dsn">The parsed DSN that identifies the relay and project.</param>
        /// <param name="configure">An optional callback that tweaks the relay options.</param>
        public HttpRelayDefinition(Dsn dsn, Action<HttpRelayOptions>? configure = null)
        {
            _dsn = dsn ?? throw new ArgumentNullException(nameof(dsn));
            _options = new HttpRelayOptions();
            configure?.Invoke(_options);
        }

        /// <summary>
        /// Creates a definition from an already built options instance. Used by
        /// <see cref="GlitchPortClient"/> for the implicit relay derived from
        /// <see cref="GlitchPortOptions.Dsn"/>.
        /// </summary>
        internal HttpRelayDefinition(Dsn dsn, HttpRelayOptions options)
        {
            _dsn = dsn ?? throw new ArgumentNullException(nameof(dsn));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc/>
        public override string Type => TypeName;

        /// <inheritdoc/>
        public override RelayOptions Options => _options;

        internal override IRelay CreateRelay(RelayCreationContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.HttpClient is null)
            {
                throw new InvalidOperationException("Creating an http relay requires an HttpClient.");
            }

            return new HttpRelay(_dsn, _options, context.HttpClient);
        }
    }

    /// <summary>Factories for the built-in relay types.</summary>
    public static partial class Relay
    {
        /// <summary>Creates an HTTP relay definition for a DSN string.</summary>
        /// <param name="dsn">The DSN that identifies the relay and project.</param>
        /// <param name="configure">An optional callback that tweaks the relay options.</param>
        /// <returns>The relay definition.</returns>
        /// <exception cref="FormatException"><paramref name="dsn"/> is not a valid DSN.</exception>
        public static RelayDefinition Http(string dsn, Action<HttpRelayOptions>? configure = null)
        {
            if (dsn is null)
            {
                throw new ArgumentNullException(nameof(dsn));
            }

            return new HttpRelayDefinition(Dsn.Parse(dsn), configure);
        }
    }
}
