namespace GlitchPort.Transport.Http;

/// <summary>
/// Thrown when an envelope batch could not be published to the Processor.
/// </summary>
public sealed class EnvelopePublishException : Exception
{
    /// <summary>
    /// Creates the exception for a failed publish.
    /// </summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="attemptedEndpoints">Endpoints attempted, in attempt order.</param>
    /// <param name="innerException">The last transport error, when available.</param>
    public EnvelopePublishException(
        string message,
        IReadOnlyList<string> attemptedEndpoints,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(attemptedEndpoints);

        AttemptedEndpoints = attemptedEndpoints.ToArray();
    }

    /// <summary>
    /// Endpoints attempted for the failed publish, in attempt order.
    /// </summary>
    public IReadOnlyList<string> AttemptedEndpoints { get; }
}
