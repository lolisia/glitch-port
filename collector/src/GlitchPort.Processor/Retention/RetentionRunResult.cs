namespace GlitchPort.Processor.Retention;

/// <summary>
/// Outcome of one executed retention run, mirroring the <c>maintenance_runs</c>
/// row the run wrote.
/// </summary>
/// <param name="StartedAt">UTC time at which the run started.</param>
/// <param name="FinishedAt">UTC time at which the run finished.</param>
/// <param name="Days">Retention window of the run in days.</param>
/// <param name="DeletedEvents">Number of expired events deleted.</param>
/// <param name="DeletedIssues">Number of empty, expired issues deleted.</param>
/// <param name="DeletedMessages">Number of expired legacy messages deleted.</param>
public sealed record RetentionRunResult(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int Days,
    long DeletedEvents,
    long DeletedIssues,
    long DeletedMessages);
