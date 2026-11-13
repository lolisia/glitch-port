using GlitchPort.Collector.Contracts;

namespace GlitchPort.Relay.Tests;

/// <summary>Records every envelope passed to the publisher.</summary>
internal sealed class RecordingEnvelopePublisher : IEnvelopePublisher
{
    private readonly List<Envelope> _envelopes = [];

    /// <summary>Envelopes received so far, in publish order.</summary>
    public IReadOnlyList<Envelope> Envelopes => _envelopes;

    /// <inheritdoc />
    public ValueTask PublishAsync(IReadOnlyList<Envelope> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        _envelopes.AddRange(batch);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A publisher that always fails with the supplied exception.</summary>
internal sealed class ThrowingEnvelopePublisher : IEnvelopePublisher
{
    private readonly Exception _exception;

    public ThrowingEnvelopePublisher(Exception exception)
    {
        _exception = exception;
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(IReadOnlyList<Envelope> batch, CancellationToken ct) =>
        ValueTask.FromException(_exception);
}
