using System;

namespace GlitchPort
{
    /// <summary>A relay definition that writes events to rotating local files.</summary>
    public sealed class FileRelayDefinition : RelayDefinition
    {
        private const string TypeName = "file";

        private readonly FileRelayOptions _options;

        /// <summary>Creates a file relay definition.</summary>
        /// <param name="configure">An optional callback that tweaks the relay options.</param>
        public FileRelayDefinition(Action<FileRelayOptions>? configure = null)
        {
            _options = new FileRelayOptions();
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

            return new FileRelay(_options);
        }
    }

    public static partial class Relay
    {
        /// <summary>Creates a definition for the local file relay.</summary>
        /// <param name="configure">An optional callback that tweaks the relay options.</param>
        /// <returns>The relay definition.</returns>
        public static RelayDefinition File(Action<FileRelayOptions>? configure = null)
        {
            return new FileRelayDefinition(configure);
        }
    }
}
