using System.Net;
using GlitchPort.Processor.Migrations;
using Npgsql;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class MigrationTests
{
    private readonly ProcessorFixture _fixture;

    public MigrationTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Startup_creates_all_tables_and_records_every_migration()
    {
        var tables = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            """
            select table_name
            from information_schema.tables
            where table_schema = 'public'
              and table_name in ('messages', 'schema_migrations', 'issues', 'events', 'labels', 'issue_labels', 'projects', 'project_keys', 'maintenance_runs')
            order by table_name
            """);

        Assert.Equal(
            ["events", "issue_labels", "issues", "labels", "maintenance_runs", "messages", "project_keys", "projects", "schema_migrations"],
            tables);

        var versions = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            "select version from schema_migrations order by version");

        Assert.Equal(
            ["001_create_messages.sql", "002_events_issues.sql", "003_projects_keys.sql", "004_maintenance_runs.sql", "005_drop_project_platform.sql", "006_regression_releases.sql"],
            versions);
    }

    [Fact]
    public async Task New_tables_have_the_expected_columns()
    {
        Assert.Equal(
            [
                "id",
                "project_id",
                "fingerprint",
                "title",
                "culprit",
                "level",
                "platform",
                "status",
                "first_seen",
                "last_seen",
                "event_count",
                "last_release",
                "last_environment",
                "first_release",
                "resolved_at",
                "resolved_in_release",
                "regressed_at",
                "regression_count",
            ],
            await ColumnsAsync("issues"));

        Assert.Equal(
            [
                "id",
                "project_id",
                "issue_id",
                "timestamp",
                "received_at",
                "level",
                "platform",
                "environment",
                "release",
                "user_id",
                "endpoint",
                "message",
                "payload",
            ],
            await ColumnsAsync("events"));

        Assert.Equal(
            ["id", "project_id", "name", "color"],
            await ColumnsAsync("labels"));

        Assert.Equal(
            ["issue_id", "label_id"],
            await ColumnsAsync("issue_labels"));

        Assert.Equal(
            ["id", "name", "slug", "created_at"],
            await ColumnsAsync("projects"));

        Assert.Equal(
            [
                "id",
                "project_id",
                "public_key",
                "label",
                "is_active",
                "rate_limit_per_minute",
                "created_at",
                "revoked_at",
            ],
            await ColumnsAsync("project_keys"));

        Assert.Equal(
            [
                "id",
                "kind",
                "started_at",
                "finished_at",
                "days",
                "deleted_events",
                "deleted_issues",
                "deleted_messages",
            ],
            await ColumnsAsync("maintenance_runs"));
    }

    [Fact]
    public async Task Messages_table_keeps_its_columns_and_stays_readable()
    {
        Assert.Equal(
            [
                "id",
                "project_id",
                "received_at",
                "endpoint",
                "message",
                "client_timestamp",
                "sdk_name",
                "sdk_version",
                "stored_at",
            ],
            await ColumnsAsync("messages"));

        var eventId = Guid.NewGuid();
        var projectId = TestData.NextProjectId();

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
            insert into messages (id, project_id, received_at, endpoint, message)
            values ('{eventId}', {projectId}, now(), 'POST /api/1/messages', 'legacy message')
            """);

        var messages = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            $"select message from messages where id = '{eventId}'");

        Assert.Equal(["legacy message"], messages);
    }

    [Fact]
    public async Task Indexes_and_the_trigram_extension_exist()
    {
        var extensions = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            "select extname from pg_extension where extname = 'pg_trgm'");

        Assert.Equal(["pg_trgm"], extensions);

        var indexes = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            """
            select indexname
            from pg_indexes
            where schemaname = 'public'
              and tablename in ('issues', 'events', 'maintenance_runs')
              and indexname like 'ix_%'
            order by indexname
            """);

        Assert.Equal(
            [
                "ix_events_issue_timestamp",
                "ix_events_project_received",
                "ix_events_project_release",
                "ix_events_received_at",
                "ix_issues_project_first_release",
                "ix_issues_project_platform",
                "ix_issues_project_status_last_seen",
                "ix_issues_title_trgm",
                "ix_maintenance_runs_kind_finished_at",
            ],
            indexes);
    }

    [Fact]
    public async Task Issue_status_check_rejects_unknown_values()
    {
        var projectId = TestData.NextProjectId();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
            insert into issues (project_id, fingerprint, title, level, platform, status, first_seen, last_seen)
            values ({projectId}, 'invalid-status', 'boom', 'error', 'unity', 'done', now(), now())
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Issue_project_and_fingerprint_are_unique()
    {
        var projectId = TestData.NextProjectId();

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
            insert into issues (project_id, fingerprint, title, level, platform, first_seen, last_seen)
            values ({projectId}, 'duplicate', 'boom', 'error', 'unity', now(), now())
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
            insert into issues (project_id, fingerprint, title, level, platform, first_seen, last_seen)
            values ({projectId}, 'duplicate', 'other', 'info', 'other', now(), now())
            """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task Labels_default_their_colour_and_are_unique_per_project()
    {
        var projectId = TestData.NextProjectId();

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"insert into labels (project_id, name) values ({projectId}, 'regression')");

        var colour = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            $"select color from labels where project_id = {projectId}");

        Assert.Equal(["#888888"], colour);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"insert into labels (project_id, name) values ({projectId}, 'regression')"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task Deleting_an_issue_or_label_cascades_to_issue_labels()
    {
        var projectId = TestData.NextProjectId();
        var eventId = Guid.NewGuid();

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
            insert into issues (project_id, fingerprint, title, level, platform, first_seen, last_seen)
            values ({projectId}, 'cascade', 'boom', 'error', 'unity', now(), now());
            insert into labels (project_id, name) values ({projectId}, 'regression');
            """);

        var issueId = await TestDatabase.QuerySingleLongAsync(
            _fixture.ConnectionString,
            $"select id from issues where project_id = {projectId}");

        var labelId = await TestDatabase.QuerySingleLongAsync(
            _fixture.ConnectionString,
            $"select id from labels where project_id = {projectId}");

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
            insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, endpoint, payload)
            values ('{eventId}', {projectId}, {issueId}, now(), now(), 'error', 'unity', 'POST /api/1/events', '[]'::jsonb);
            insert into issue_labels (issue_id, label_id) values ({issueId}, {labelId});
            """);

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"delete from issues where id = {issueId}");

        Assert.Equal(
            0,
            await TestDatabase.QuerySingleLongAsync(
                _fixture.ConnectionString,
                $"select count(*) from events where id = '{eventId}'"));
        Assert.Equal(
            0,
            await TestDatabase.QuerySingleLongAsync(
                _fixture.ConnectionString,
                $"select count(*) from issue_labels where label_id = {labelId}"));

        // A label that is still referenced disappears from issue_labels when
        // the label itself is deleted.
        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"""
            insert into issues (project_id, fingerprint, title, level, platform, first_seen, last_seen)
            values ({projectId}, 'cascade-2', 'boom', 'error', 'unity', now(), now());
            """);

        var secondIssueId = await TestDatabase.QuerySingleLongAsync(
            _fixture.ConnectionString,
            $"select id from issues where project_id = {projectId}");

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"insert into issue_labels (issue_id, label_id) values ({secondIssueId}, {labelId})");

        await TestDatabase.ExecuteAsync(
            _fixture.ConnectionString,
            $"delete from labels where id = {labelId}");

        Assert.Equal(
            0,
            await TestDatabase.QuerySingleLongAsync(
                _fixture.ConnectionString,
                $"select count(*) from issue_labels where issue_id = {secondIssueId}"));
    }

    [Fact]
    public async Task Second_startup_is_a_noop()
    {
        using var factory = new ProcessorApplicationFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var versions = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            "select version from schema_migrations order by version");

        Assert.Equal(
            ["001_create_messages.sql", "002_events_issues.sql", "003_projects_keys.sql", "004_maintenance_runs.sql", "005_drop_project_platform.sql", "006_regression_releases.sql"],
            versions);
    }

    [Fact]
    public async Task Concurrent_migrators_apply_each_script_exactly_once()
    {
        var databaseName = $"migrator_{Guid.NewGuid():N}";
        await TestDatabase.ExecuteAsync(_fixture.ConnectionString, $"create database \"{databaseName}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;

        var migrationsDirectory = Path.Combine(
            AppContext.BaseDirectory,
            DatabaseMigrator.MigrationsDirectoryName);

        try
        {
            var results = await Task.WhenAll(
                DatabaseMigrator.ApplyAsync(connectionString, migrationsDirectory),
                DatabaseMigrator.ApplyAsync(connectionString, migrationsDirectory));

            var appliedByThisRun = results
                .SelectMany(versions => versions)
                .OrderBy(version => version, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                ["001_create_messages.sql", "002_events_issues.sql", "003_projects_keys.sql", "004_maintenance_runs.sql", "005_drop_project_platform.sql", "006_regression_releases.sql"],
                appliedByThisRun);

            var recordedVersions = await TestDatabase.QueryStringsAsync(
                connectionString,
                "select version from schema_migrations order by version");

            Assert.Equal(
                ["001_create_messages.sql", "002_events_issues.sql", "003_projects_keys.sql", "004_maintenance_runs.sql", "005_drop_project_platform.sql", "006_regression_releases.sql"],
                recordedVersions);
        }
        finally
        {
            await TestDatabase.ExecuteAsync(
                _fixture.ConnectionString,
                $"drop database if exists \"{databaseName}\" with (force)");
        }
    }

    [Fact]
    public async Task Regression_release_migration_backfills_first_release_from_the_earliest_event()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        var earlyMigrations = Directory.CreateTempSubdirectory("glitchport-migrations-");

        try
        {
            CopyMigrations(earlyMigrations.FullName, "001_create_messages.sql", "002_events_issues.sql");
            await DatabaseMigrator.ApplyAsync(database.ConnectionString, earlyMigrations.FullName);

            await TestDatabase.ExecuteAsync(
                database.ConnectionString,
                """
                insert into issues (project_id, fingerprint, title, level, platform, first_seen, last_seen, last_release)
                values (1, 'fp-earliest', 'earliest', 'error', 'unity',
                        '2026-01-01T12:00:00+00:00', '2026-01-01T13:00:00+00:00', '2.0'),
                       (2, 'fp-releaseless-earliest', 'releaseless earliest', 'error', 'unity',
                        '2026-01-01T12:00:00+00:00', '2026-01-01T13:00:00+00:00', '2.0'),
                       (3, 'fp-last-release-only', 'last release only', 'error', 'unity',
                        '2026-01-01T12:00:00+00:00', '2026-01-01T12:00:00+00:00', '3.0'),
                       (4, 'fp-no-release', 'no release', 'error', 'unity',
                        '2026-01-01T12:00:00+00:00', '2026-01-01T12:00:00+00:00', null),
                       (5, 'fp-received-tie', 'received tie', 'error', 'unity',
                        '2026-01-01T12:00:00+00:00', '2026-01-01T12:00:00+00:00', 'late');

                insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, release, endpoint, payload)
                select gen_random_uuid(), 1, id, '2026-01-01T13:00:00+00:00', '2026-01-01T13:00:00+00:00',
                       'error', 'unity', '2.0', 'POST /api/1/events', '{}'::jsonb
                from issues where fingerprint = 'fp-earliest';

                insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, release, endpoint, payload)
                select gen_random_uuid(), 1, id, '2026-01-01T12:00:00+00:00', '2026-01-01T12:00:00+00:00',
                       'error', 'unity', '1.0', 'POST /api/1/events', '{}'::jsonb
                from issues where fingerprint = 'fp-earliest';

                insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, release, endpoint, payload)
                select gen_random_uuid(), 2, id, '2026-01-01T12:00:00+00:00', '2026-01-01T12:00:00+00:00',
                       'error', 'unity', null, 'POST /api/1/events', '{}'::jsonb
                from issues where fingerprint = 'fp-releaseless-earliest';

                insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, release, endpoint, payload)
                select gen_random_uuid(), 2, id, '2026-01-01T13:00:00+00:00', '2026-01-01T13:00:00+00:00',
                       'error', 'unity', '2.0', 'POST /api/1/events', '{}'::jsonb
                from issues where fingerprint = 'fp-releaseless-earliest';

                insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, release, endpoint, payload)
                select gen_random_uuid(), 5, id, '2026-01-01T12:00:00+00:00', '2026-01-01T12:05:00+00:00',
                       'error', 'unity', 'late', 'POST /api/1/events', '{}'::jsonb
                from issues where fingerprint = 'fp-received-tie';

                insert into events (id, project_id, issue_id, timestamp, received_at, level, platform, release, endpoint, payload)
                select gen_random_uuid(), 5, id, '2026-01-01T12:00:00+00:00', '2026-01-01T12:01:00+00:00',
                       'error', 'unity', 'early', 'POST /api/1/events', '{}'::jsonb
                from issues where fingerprint = 'fp-received-tie';
                """);

            var applied = await DatabaseMigrator.ApplyAsync(
                database.ConnectionString,
                DatabaseMigrator.DefaultDirectory);

            Assert.Contains("006_regression_releases.sql", applied);

            var issues = await TestDatabase.QueryStringsAsync(
                database.ConnectionString,
                """
                select fingerprint || '|' || coalesce(first_release, '<null>') || '|' || regression_count
                from issues
                order by id
                """);

            Assert.Equal(
                [
                    "fp-earliest|1.0|0",
                    "fp-releaseless-earliest|2.0|0",
                    "fp-last-release-only|3.0|0",
                    "fp-no-release|<null>|0",
                    "fp-received-tie|early|0",
                ],
                issues);

            // The new columns start empty; only the backfill touches them.
            Assert.Equal(
                0,
                await TestDatabase.QuerySingleLongAsync(
                    database.ConnectionString,
                    """
                    select count(*)
                    from issues
                    where resolved_at is not null
                       or resolved_in_release is not null
                       or regressed_at is not null
                    """));
        }
        finally
        {
            earlyMigrations.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Regression_release_migration_adds_the_release_indexes()
    {
        var indexes = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            """
            select indexdef
            from pg_indexes
            where schemaname = 'public'
              and indexname in ('ix_events_project_release', 'ix_issues_project_first_release')
            order by indexname
            """);

        Assert.Equal(
            [
                "CREATE INDEX ix_events_project_release ON public.events USING btree (project_id, release) WHERE (release IS NOT NULL)",
                "CREATE INDEX ix_issues_project_first_release ON public.issues USING btree (project_id, first_release)",
            ],
            indexes);
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

    private Task<List<string>> ColumnsAsync(string tableName) =>
        TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            $"""
            select column_name
            from information_schema.columns
            where table_schema = 'public' and table_name = '{tableName}'
            order by ordinal_position
            """);
}
