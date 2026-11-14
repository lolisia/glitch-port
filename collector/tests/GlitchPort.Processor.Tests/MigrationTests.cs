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
    public async Task Startup_creates_all_tables_and_records_both_migrations()
    {
        var tables = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            """
            select table_name
            from information_schema.tables
            where table_schema = 'public'
              and table_name in ('messages', 'schema_migrations', 'issues', 'events', 'labels', 'issue_labels')
            order by table_name
            """);

        Assert.Equal(
            ["events", "issue_labels", "issues", "labels", "messages", "schema_migrations"],
            tables);

        var versions = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            "select version from schema_migrations order by version");

        Assert.Equal(["001_create_messages.sql", "002_events_issues.sql"], versions);
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
              and tablename in ('issues', 'events')
              and indexname like 'ix_%'
            order by indexname
            """);

        Assert.Equal(
            [
                "ix_events_issue_timestamp",
                "ix_events_project_received",
                "ix_issues_project_status_last_seen",
                "ix_issues_title_trgm",
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

        Assert.Equal(["001_create_messages.sql", "002_events_issues.sql"], versions);
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
                ["001_create_messages.sql", "002_events_issues.sql"],
                appliedByThisRun);

            var recordedVersions = await TestDatabase.QueryStringsAsync(
                connectionString,
                "select version from schema_migrations order by version");

            Assert.Equal(
                ["001_create_messages.sql", "002_events_issues.sql"],
                recordedVersions);
        }
        finally
        {
            await TestDatabase.ExecuteAsync(
                _fixture.ConnectionString,
                $"drop database if exists \"{databaseName}\" with (force)");
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
