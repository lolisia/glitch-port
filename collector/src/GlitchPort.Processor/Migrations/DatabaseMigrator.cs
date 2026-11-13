using Dapper;
using Npgsql;

namespace GlitchPort.Processor.Migrations;

/// <summary>
/// Applies the SQL migration scripts shipped in the <c>Migrations</c> directory.
/// </summary>
/// <remarks>
/// Migrations are serialized across Processor instances with the PostgreSQL
/// advisory lock <see cref="AdvisoryLockKey"/>. Every script runs in its own
/// transaction together with the <c>schema_migrations</c> insert, so a failed
/// script leaves neither schema changes nor a version row behind. Starting the
/// service repeatedly, or two instances at the same time, therefore applies
/// each script exactly once.
/// </remarks>
public static class DatabaseMigrator
{
    /// <summary>Session advisory lock key that serializes migrations.</summary>
    public const long AdvisoryLockKey = 823651;

    /// <summary>Name of the directory that contains the <c>*.sql</c> migration scripts.</summary>
    public const string MigrationsDirectoryName = "Migrations";

    private const string CreateSchemaMigrationsTableSql = """
        create table if not exists schema_migrations (
            version text primary key,
            applied_at timestamptz not null default now()
        )
        """;

    private static readonly string LockSql = $"select pg_advisory_lock({AdvisoryLockKey})";
    private static readonly string UnlockSql = $"select pg_advisory_unlock({AdvisoryLockKey})";
    private const string AppliedVersionsSql = "select version from schema_migrations";
    private const string RecordVersionSql = "insert into schema_migrations (version) values (@Version)";

    /// <summary>
    /// Directory the application copies its migration scripts to.
    /// </summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, MigrationsDirectoryName);

    /// <summary>
    /// Applies every migration script that has not been recorded in
    /// <c>schema_migrations</c> yet.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="migrationsDirectory">
    /// Directory containing the <c>*.sql</c> scripts; defaults to <see cref="DefaultDirectory"/>.
    /// </param>
    /// <param name="logger">Optional logger for progress and diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token for the whole operation.</param>
    /// <returns>Versions (file names) applied by this invocation, in ordinal order.</returns>
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        string connectionString,
        string? migrationsDirectory = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var scripts = LoadScripts(migrationsDirectory ?? DefaultDirectory);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection
            .ExecuteAsync(new CommandDefinition(LockSql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        try
        {
            // Creating the table under the advisory lock keeps the very first
            // concurrent start race-free as well.
            await connection
                .ExecuteAsync(new CommandDefinition(CreateSchemaMigrationsTableSql, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            var appliedVersions = (await connection
                    .QueryAsync<string>(new CommandDefinition(AppliedVersionsSql, cancellationToken: cancellationToken))
                    .ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            var newlyApplied = new List<string>(scripts.Count);

            foreach (var script in scripts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (appliedVersions.Contains(script.Version))
                {
                    continue;
                }

                await ApplyScriptAsync(connection, script, cancellationToken).ConfigureAwait(false);
                newlyApplied.Add(script.Version);

                logger?.LogInformation("Applied database migration {Version}.", script.Version);
            }

            return newlyApplied;
        }
        finally
        {
            await ReleaseLockAsync(connection, logger).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<MigrationScript> LoadScripts(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Migrations directory '{directory}' was not found.");
        }

        return Directory
            .EnumerateFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new MigrationScript(Path.GetFileName(path), File.ReadAllText(path)))
            .ToList();
    }

    private static async Task ApplyScriptAsync(
        NpgsqlConnection connection,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await connection
                .ExecuteAsync(new CommandDefinition(script.Sql, transaction: transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await connection
                .ExecuteAsync(new CommandDefinition(
                    RecordVersionSql,
                    new { script.Version },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection, ILogger? logger)
    {
        try
        {
            // Use CancellationToken.None: the lock must be released even when the
            // caller's token has already fired. A broken connection releases the
            // session-scoped lock automatically when it is closed.
            await connection
                .ExecuteAsync(new CommandDefinition(UnlockSql, cancellationToken: CancellationToken.None))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            logger?.LogWarning(exception, "Could not release the migration advisory lock explicitly.");
        }
    }

    private sealed record MigrationScript(string Version, string Sql);
}
