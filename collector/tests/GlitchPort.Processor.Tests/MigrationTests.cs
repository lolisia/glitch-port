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
    public async Task Startup_creates_messages_and_schema_migrations()
    {
        var tables = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            """
            select table_name
            from information_schema.tables
            where table_schema = 'public'
              and table_name in ('messages', 'schema_migrations')
            order by table_name
            """);

        Assert.Equal(["messages", "schema_migrations"], tables);

        var versions = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            "select version from schema_migrations order by version");

        Assert.Equal(["001_create_messages.sql"], versions);
    }

    [Fact]
    public async Task Messages_table_has_the_expected_columns()
    {
        var columns = await TestDatabase.QueryStringsAsync(
            _fixture.ConnectionString,
            """
            select column_name
            from information_schema.columns
            where table_schema = 'public' and table_name = 'messages'
            order by ordinal_position
            """);

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
            columns);
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

        Assert.Equal(["001_create_messages.sql"], versions);
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

        var results = await Task.WhenAll(
            DatabaseMigrator.ApplyAsync(connectionString, migrationsDirectory),
            DatabaseMigrator.ApplyAsync(connectionString, migrationsDirectory));

        var appliedByThisRun = results
            .SelectMany(versions => versions)
            .OrderBy(version => version, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["001_create_messages.sql"], appliedByThisRun);

        var recordedVersions = await TestDatabase.QueryStringsAsync(
            connectionString,
            "select version from schema_migrations order by version");

        Assert.Equal(["001_create_messages.sql"], recordedVersions);

        await TestDatabase.ExecuteAsync(_fixture.ConnectionString, $"drop database \"{databaseName}\"");
    }
}
