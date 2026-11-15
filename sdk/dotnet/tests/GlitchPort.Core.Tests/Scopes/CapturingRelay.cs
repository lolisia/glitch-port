using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GlitchPort.Tests.Scopes
{
    /// <summary>Thread-safe list of the events a capturing relay accepted.</summary>
    internal sealed class CapturedEventSink
    {
        private readonly object _gate = new object();
        private readonly List<CapturedEvent> _events = new List<CapturedEvent>();

        public IReadOnlyList<CapturedEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Add(CapturedEvent e)
        {
            lock (_gate)
            {
                _events.Add(e);
            }
        }
    }

    /// <summary>
    /// A relay definition that keeps every <see cref="CapturedEvent"/> in memory so
    /// tests can assert on the display data the SDK exposes to relays without an
    /// HTTP round-trip.
    /// </summary>
    internal sealed class CapturingRelayDefinition : RelayDefinition
    {
        private readonly CapturingRelayOptions _options = new CapturingRelayOptions();

        public CapturedEventSink Sink { get; } = new CapturedEventSink();

        public override string Type => "capture";

        public override RelayOptions Options => _options;

        internal override IRelay CreateRelay(RelayCreationContext context)
        {
            return new CapturingRelay(Sink, _options);
        }

        private sealed class CapturingRelayOptions : RelayOptions
        {
        }

        private sealed class CapturingRelay : IRelay
        {
            private readonly CapturedEventSink _sink;
            private readonly CapturingRelayOptions _options;

            public CapturingRelay(CapturedEventSink sink, CapturingRelayOptions options)
            {
                _sink = sink;
                _options = options;
            }

            public string Name => "capture";

            public string Type => "capture";

            public Level MinLevel => _options.MinLevel;

            public bool TryEnqueue(CapturedEvent e)
            {
                _sink.Add(e);
                return true;
            }

            public Task<RelayDeliveryResult> FlushAsync(TimeSpan timeout)
            {
                return Task.FromResult(new RelayDeliveryResult("capture", "capture", _sink.Events.Count, 0, 0, null));
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Creates a client whose only relay captures events in memory.</summary>
    internal static class CapturingClient
    {
        public static GlitchPortClient Create(
            out CapturingRelayDefinition relay,
            Action<GlitchPortOptions>? configure = null)
        {
            relay = new CapturingRelayDefinition();
            var options = new GlitchPortOptions();
            options.Relays.Add(relay);
            configure?.Invoke(options);
            return new GlitchPortClient(options);
        }
    }
}
