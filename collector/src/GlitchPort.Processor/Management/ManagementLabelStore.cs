using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Lists, creates, updates and deletes project labels.
/// </summary>
internal sealed class ManagementLabelStore
{
    private const string SelectLabelColumns = """
        id as "Id",
        name as "Name",
        color as "Color"
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a store that opens connections from <paramref name="dataSource"/>.
    /// </summary>
    public ManagementLabelStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Returns the labels of a project ordered by name.
    /// </summary>
    public async Task<List<LabelResponse>> ListAsync(long projectId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var labels = await connection
            .QueryAsync<LabelResponse>(new CommandDefinition(
                $"select {SelectLabelColumns} from labels where project_id = @ProjectId order by name",
                new { ProjectId = projectId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return labels.ToList();
    }

    /// <summary>
    /// Creates a label. Names are unique per project.
    /// </summary>
    public async Task<LabelMutationResult> CreateAsync(
        long projectId,
        string name,
        string color,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var label = await connection
                .QuerySingleAsync<LabelResponse>(new CommandDefinition(
                    """
                    insert into labels (project_id, name, color)
                    values (@ProjectId, @Name, @Color)
                    returning id as "Id", name as "Name", color as "Color"
                    """,
                    new { ProjectId = projectId, Name = name, Color = color },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            return new LabelMutationResult(LabelMutationStatus.Ok, label);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new LabelMutationResult(LabelMutationStatus.Conflict, null);
        }
    }

    /// <summary>
    /// Changes the name and/or color of a label.
    /// </summary>
    public async Task<LabelMutationResult> UpdateAsync(
        long labelId,
        string? name,
        string? color,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var updated = await connection
                .QuerySingleOrDefaultAsync<LabelResponse>(new CommandDefinition(
                    """
                    update labels
                    set name = coalesce(@Name::text, name),
                        color = coalesce(@Color::text, color)
                    where id = @LabelId
                    returning id as "Id", name as "Name", color as "Color"
                    """,
                    new { LabelId = labelId, Name = name, Color = color },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            return updated is null
                ? new LabelMutationResult(LabelMutationStatus.NotFound, null)
                : new LabelMutationResult(LabelMutationStatus.Ok, updated);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new LabelMutationResult(LabelMutationStatus.Conflict, null);
        }
    }

    /// <summary>
    /// Deletes a label. Its <c>issue_labels</c> rows are removed by the
    /// foreign key.
    /// </summary>
    public async Task<bool> DeleteAsync(long labelId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection
            .ExecuteAsync(new CommandDefinition(
                "delete from labels where id = @LabelId",
                new { LabelId = labelId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return affected > 0;
    }
}
