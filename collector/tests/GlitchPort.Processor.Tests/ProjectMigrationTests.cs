using GlitchPort.Processor.Migrations;
using Npgsql;
using Xunit;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// Owns a database created next to the fixture database so that a test can
/// apply migrations from a known, empty state. The database is dropped again
/// when the handle is disposed.
/// </summary>
internal sealed class ScratchDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private ScratchDatabase(
        string adminConnectionString,
        string databaseName,
        string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    /// <summary>Connection string of the scratch database.</summary>
    public string ConnectionString { get; }

    /// <summary>Creates an empty database and returns its handle.</summary>
    public static async Task<ScratchDatabase> CreateAsync(string adminConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminConnectionString);

        var databaseName = $"scratch_{Guid.NewGuid():N}";
        await TestDatabase.ExecuteAsync(adminConnectionString, $"create database \"{databaseName}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;

        return new ScratchDatabase(adminConnectionString, databaseName, connectionString);
    }

    /// <summary>Drops the database, even when connections are still open.</summary>
    public async ValueTask DisposeAsync() =>
        await TestDatabase.ExecuteAsync(
            _adminConnectionString,
            $"drop database if exists \"{_databaseName}\" with (force)");
}

[Collection(ProcessorCollection.Name)]
public sealed class ProjectMigrationTests
{
    private readonly ProcessorFixture _fixture;

    public ProjectMigrationTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Projects_migration_backfills_existing_ids_and_adds_cascading_foreign_keys()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        var earlyMigrations = Directory.CreateTempSubdirectory("glitchport-migrations-");

        try
        {
            // 001 and 002 describe the schema before projects existed; the rows
            // inserted below are the data 003 has to keep valid.
            CopyMigrations(
                earlyMigrations.FullName,
                "001_create_messages.sql",
                "002_events_issues.sql");

            var applied = await DatabaseMigrator.ApplyAsync(
                database.ConnectionString,
                earlyMigrations.FullName);

            Assert.Equal(["001_create_messages.sql", "002_events_issues.sql"], applied);

            await InsertRowsWithoutProjectsAsync(database.ConnectionString);

            // The full directory applies 003 onwards only, because 001 and 002
            // are already recorded in schema_migrations.
            var projectsMigration = await DatabaseMigrator.ApplyAsync(
                database.ConnectionString,
                DatabaseMigrator.DefaultDirectory);

            Assert.Equal(
                ["003_projects_keys.sql", "004_maintenance_runs.sql", "005_drop_project_platform.sql", "006_regression_releases.sql", "007_alerts.sql"],
                projectsMigration);

            // The maintenance bookkeeping table arrives with migration 004.
            Assert.Equal(
                1,
                await TestDatabase.QuerySingleLongAsync(
                    database.ConnectionString,
                    """
                    select count(*)
                    from information_schema.tables
                    where table_schema = 'public' and table_name = 'maintenance_runs'
                    """));

            // Migration 005 removes the project platform; platforms now live
            // on issues, which are indexed for the platform filter.
            Assert.Equal(
                0,
                await TestDatabase.QuerySingleLongAsync(
                    database.ConnectionString,
                    """
                    select count(*)
                    from information_schema.columns
                    where table_schema = 'public'
                      and table_name = 'projects'
                      and column_name = 'platform'
                    """));
            Assert.Equal(
                1,
                await TestDatabase.QuerySingleLongAsync(
                    database.ConnectionString,
                    """
                    select count(*)
                    from pg_indexes
                    where schemaname = 'public'
                      and tablename = 'issues'
                      and indexname = 'ix_issues_project_platform'
                    """));

            // Every referenced id became a project of its own...
            var projects = await TestDatabase.QueryStringsAsync(
                database.ConnectionString,
                "select id || '|' || name || '|' || slug from projects order by id");

            Assert.Equal(["1|Project 1|project-1", "7|Project 7|project-7"], projects);

            // ...and the rows that reference them survived.
            foreach (var projectId in new[] { 1L, 7L })
            {
                Assert.Equal(1, await CountAsync(database.ConnectionString, "issues", projectId));
                Assert.Equal(1, await CountAsync(database.ConnectionString, "events", projectId));
                Assert.Equal(1, await CountAsync(database.ConnectionString, "labels", projectId));
                Assert.Equal(1, await CountAsync(database.ConnectionString, "messages", projectId));
            }

            // Issues, events and labels reference projects and cascade...
            var foreignKeys = await TestDatabase.QueryStringsAsync(
                database.ConnectionString,
                """
                select conname || '|' || conrelid::regclass::text || '|' || confrelid::regclass::text || '|' || confdeltype::text
                from pg_constraint
                where contype = 'f'
                  and conname in ('fk_events_project_id', 'fk_issues_project_id', 'fk_labels_project_id')
                order by conname
                """);

            Assert.Equal(
                [
                    "fk_events_project_id|events|projects|c",
                    "fk_issues_project_id|issues|projects|c",
                    "fk_labels_project_id|labels|projects|c",
                ],
                foreignKeys);

            // ...while the legacy messages table deliberately has no project FK.
            Assert.Equal(
                0,
                await TestDatabase.QuerySingleLongAsync(
                    database.ConnectionString,
                    "select count(*) from pg_constraint where contype = 'f' and conrelid = 'messages'::regclass"));

            // The identity sequence continues above the backfilled ids.
            var nextProjectId = await TestDatabase.QuerySingleLongAsync(
                database.ConnectionString,
                "select nextval(pg_get_serial_sequence('projects', 'id'))");
            var maxProjectId = await TestDatabase.QuerySingleLongAsync(
                database.ConnectionString,
                "select coalesce(max(id), 0) from projects");

            Assert.True(nextProjectId > maxProjectId);

            // Deleting a project removes its issues, events and labels, but a
            // message written before projects existed is retained.
            await TestDatabase.ExecuteAsync(database.ConnectionString, "delete from projects where id = 7");

            Assert.Equal(0, await CountAsync(database.ConnectionString, "issues", 7));
            Assert.Equal(0, await CountAsync(database.ConnectionString, "events", 7));
            Assert.Equal(0, await CountAsync(database.ConnectionString, "labels", 7));
            Assert.Equal(1, await CountAsync(database.ConnectionString, "messages", 7));
            Assert.Equal(1, await CountAsync(database.ConnectionString, "issues", 1));
        }
        finally
        {
            earlyMigrations.Delete(recursive: true);
        }
    }

    private static void CopyMigrations(string targetDirectory, params string[] versions)
    {
        foreach (var version in versions)
        {
            File.Copy(
                Path.Combine(DatabaseMigrator.DefaultDirectory, version),
                Path.Combine(targetDirectory, version));
        }
    }

    /// <summary>
    /// Inserts an issue, event, label and message for project ids 1 and 7, the
    /// ids a database written before migration 003 would contain.
    /// </summary>
    private static async Task InsertRowsWithoutProjectsAsync(string connectionString)
    {
        var firstEventId = Guid.NewGuid();
        var secondEventId = Guid.NewGuid();
        var firstMessageId = Guid.NewGuid();
        var secondMessageId = Guid.NewGuid();

        await TestDatabase.ExecuteAsync(
            connectionString,
            $"""
            insert into issues (project_id, fingerprint, title, level, platform, first_seen, last_seen)
            values (1, 'fingerprint-1', 'boom', 'error', 'unity', now(), now()),
                   (7, 'fingerprint-7', 'boom', 'error', 'unity', now(), now());

            insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, endpoint, payload)
            select '{firstEventId}', 1, id, now(), now(), 'error', 'unity', 'POST /api/1/events', 'null'::jsonb
            from issues
            where project_id = 1;

            insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, endpoint, payload)
            select '{secondEventId}', 7, id, now(), now(), 'error', 'unity', 'POST /api/1/events', 'null'::jsonb
            from issues
            where project_id = 7;

            insert into labels (project_id, name) values (1, 'regression'), (7, 'regression');

            insert into messages (id, project_id, received_at, endpoint, message)
            values ('{firstMessageId}', 1, now(), 'POST /api/1/messages', 'legacy-1'),
                   ('{secondMessageId}', 7, now(), 'POST /api/1/messages', 'legacy-7');
            """);
    }

    private static Task<long> CountAsync(string connectionString, string table, long projectId) =>
        TestDatabase.QuerySingleLongAsync(
            connectionString,
            $"select count(*) from {table} where project_id = {projectId}");
}
