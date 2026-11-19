using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Projects;

/// <summary>
/// Seeds the projects and public keys configured under
/// <c>Processor:Bootstrap:Projects</c> into an empty database when the
/// Processor starts.
/// </summary>
/// <remarks>
/// <para>
/// The bootstrap gives a fresh deployment (and the local development
/// environment) a DSN before any project has been created through the
/// management API. It only runs while the <c>projects</c> table is empty: once
/// any project exists, projects and keys are managed exclusively through the
/// management API, so a configured project that was deleted is never
/// recreated by a later restart.
/// </para>
/// <para>
/// The inserts use explicit project ids with <c>on conflict do nothing</c>. Two
/// instances starting at the same time serialize on the session advisory lock
/// <see cref="AdvisoryLockKey"/>; the second one finds the seeded projects and
/// skips. The key is distinct from the migration, retention and alert
/// evaluation locks.
/// </para>
/// </remarks>
public static class ProjectBootstrapper
{
    /// <summary>Session advisory lock key that serializes bootstraps.</summary>
    public const long AdvisoryLockKey = 823654;

    private static readonly string LockSql = $"select pg_advisory_lock({AdvisoryLockKey})";
    private static readonly string UnlockSql = $"select pg_advisory_unlock({AdvisoryLockKey})";

    private const string AnyProjectSql = "select exists (select 1 from projects)";

    private const string InsertProjectSql = """
        insert into projects (id, name, slug)
        values (@Id, @Name, @Slug)
        on conflict do nothing
        """;

    // Keys are only inserted for projects that exist, so a configured id whose
    // insert was skipped (for example because its slug is already taken by a
    // project created through the management API) does not break startup.
    private const string InsertKeySql = """
        insert into project_keys (project_id, public_key)
        select @ProjectId, @PublicKey
        where exists (select 1 from projects where id = @ProjectId)
        on conflict do nothing
        """;

    // Projects were inserted with explicit ids, which does not advance the
    // identity sequence; align it so the next generated id cannot collide.
    private const string AlignProjectSequenceSql = """
        select setval(pg_get_serial_sequence('projects', 'id'), coalesce(max(id), 0) + 1, false)
        from projects
        """;

    /// <summary>
    /// Inserts the configured projects and keys when no project exists yet;
    /// does nothing otherwise.
    /// </summary>
    /// <param name="dataSource">Data source of the Processor database.</param>
    /// <param name="projects">Configured projects, already bound from configuration.</param>
    /// <param name="logger">Optional logger for progress and diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token for the whole operation.</param>
    /// <exception cref="InvalidOperationException">
    /// A configured project is missing its id, name, slug or a public key.
    /// </exception>
    public static async Task BootstrapAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<ProjectBootstrapProject> projects,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(projects);

        if (projects.Count == 0)
        {
            return;
        }

        Validate(projects);

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection
            .ExecuteAsync(new CommandDefinition(LockSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        try
        {
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var anyProject = await connection
                    .ExecuteScalarAsync<bool>(new CommandDefinition(
                        AnyProjectSql,
                        transaction: transaction,
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);

                if (anyProject)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    logger?.LogInformation(
                        "Skipped the project bootstrap: the database already contains projects.");
                    return;
                }

                foreach (var project in projects)
                {
                    await connection
                        .ExecuteAsync(new CommandDefinition(
                            InsertProjectSql,
                            new { project.Id, project.Name, project.Slug },
                            transaction,
                            cancellationToken: cancellationToken))
                        .ConfigureAwait(false);

                    foreach (var publicKey in project.PublicKeys ?? [])
                    {
                        await connection
                            .ExecuteAsync(new CommandDefinition(
                                InsertKeySql,
                                new { ProjectId = project.Id, PublicKey = publicKey },
                                transaction,
                                cancellationToken: cancellationToken))
                            .ConfigureAwait(false);
                    }
                }

                await connection
                    .ExecuteAsync(new CommandDefinition(
                        AlignProjectSequenceSql,
                        transaction: transaction,
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }

            logger?.LogInformation(
                "Bootstrapped {ProjectCount} project(s) and their public keys.",
                projects.Count);
        }
        finally
        {
            await ReleaseLockAsync(connection, logger).ConfigureAwait(false);
        }
    }

    private static void Validate(IReadOnlyList<ProjectBootstrapProject> projects)
    {
        foreach (var project in projects)
        {
            if (project.Id <= 0)
            {
                throw new InvalidOperationException(
                    "Every project under 'Processor:Bootstrap:Projects' needs a positive 'Id'.");
            }

            if (string.IsNullOrWhiteSpace(project.Name))
            {
                throw new InvalidOperationException(
                    $"Project {project.Id} under 'Processor:Bootstrap:Projects' needs a 'Name'.");
            }

            if (string.IsNullOrWhiteSpace(project.Slug))
            {
                throw new InvalidOperationException(
                    $"Project {project.Id} under 'Processor:Bootstrap:Projects' needs a 'Slug'.");
            }

            foreach (var publicKey in project.PublicKeys ?? [])
            {
                if (string.IsNullOrWhiteSpace(publicKey))
                {
                    throw new InvalidOperationException(
                        $"Project {project.Id} under 'Processor:Bootstrap:Projects' contains a blank public key.");
                }
            }
        }
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection, ILogger? logger)
    {
        try
        {
            // Use CancellationToken.None: the lock must be released even when
            // the caller's token has already fired. A broken connection
            // releases the session-scoped lock when it is closed.
            await connection
                .ExecuteAsync(new CommandDefinition(UnlockSql, cancellationToken: CancellationToken.None))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            logger?.LogWarning(exception, "Could not release the bootstrap advisory lock explicitly.");
        }
    }

    private static async Task RollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // The connection is already unusable; PostgreSQL rolls back when
            // it is closed and the next attempt opens a new one.
        }
    }
}
