namespace GlitchPort.Processor.Data;

/// <summary>
/// The outcome of folding the inserted events of one batch into an issue.
/// </summary>
/// <remarks>
/// One change is produced per issue that gained at least one event, so event
/// rows suppressed by the <c>on conflict (id) do nothing</c> clause never
/// produce a change or move a counter. The change is built inside the batch
/// transaction, before it commits.
/// </remarks>
/// <param name="IssueId">Id of the updated issue.</param>
/// <param name="ProjectId">Project that owns the issue.</param>
/// <param name="Created">Whether this batch created the issue.</param>
/// <param name="Regressed">Whether this batch reopened the resolved issue.</param>
/// <param name="Level">Issue level after the update.</param>
/// <param name="Environment">Environment of the issue's most recent event after the update.</param>
/// <param name="RegressedAt">Regression time after the update, if the issue ever regressed.</param>
public sealed record IssueChange(
    long IssueId,
    long ProjectId,
    bool Created,
    bool Regressed,
    string Level,
    string? Environment,
    DateTimeOffset? RegressedAt);
