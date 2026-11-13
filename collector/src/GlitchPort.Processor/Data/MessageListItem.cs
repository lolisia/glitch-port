namespace GlitchPort.Processor.Data;

/// <summary>
/// One row of the management message list.
/// </summary>
public sealed record MessageListItem(
    Guid Id,
    DateTimeOffset ReceivedAt,
    string Endpoint,
    string Message);
