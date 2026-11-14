using System;

namespace GlitchPort
{
    /// <summary>A relay definition for the local terminal renderer.</summary>
    public sealed class TerminalRelayDefinition : RelayDefinition
    {
        private const string TypeName = "terminal";

        private readonly TerminalRelayOptions _options;

        /// <summary>Creates a terminal relay definition.</summary>
        /// <param name="configure">An optional callback that tweaks the relay options.</param>
        public TerminalRelayDefinition(Action<TerminalRelayOptions>? configure = null)
        {
            _options = new TerminalRelayOptions();
            configure?.Invoke(_options);
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

            return new TerminalRelay(_options);
        }
    }

    /// <summary>Factories for the built-in relay types.</summary>
    public static partial class Relay
    {
        /// <summary>Creates a terminal relay definition that renders events in a local console.</summary>
        /// <param name="configure">An optional callback that tweaks the relay options.</param>
        /// <returns>The relay definition.</returns>
        public static RelayDefinition Terminal(Action<TerminalRelayOptions>? configure = null)
        {
            return new TerminalRelayDefinition(configure);
        }
    }
}
