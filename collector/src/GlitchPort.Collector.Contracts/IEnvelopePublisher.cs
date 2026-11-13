namespace GlitchPort.Collector.Contracts;

/// <summary>
/// Publishes batches of envelopes from the Relay to the Processor.
/// </summary>
public interface IEnvelopePublisher
{
    /// <summary>
    /// Publishes <paramref name="batch"/> to the next available Processor.
    /// </summary>
    /// <param name="batch">Envelopes to publish. The list is not modified.</param>
    /// <param name="ct">Cancels the publish operation.</param>
    ValueTask PublishAsync(IReadOnlyList<Envelope> batch, CancellationToken ct);
}
