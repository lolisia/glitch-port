using GlitchPort.Processor.Migrations;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ProjectInboundFilterMigrationTests
{
    private readonly ProcessorFixture _fixture;

    public ProjectInboundFilterMigrationTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migration_008_adds_empty_array_filter_columns_with_a_not_null_default()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        var earlierMigrations = Directory.CreateTempSubdirectory("glitchport-migrations-");

        try
        {
            CopyMigrations(
                earlierMigrations.FullName,
                "001_create_messages.sql",
                "002_events_issues.sql",
                "003_projects_keys.sql",
                "004_maintenance_runs.sql",
                "005_drop_project_platform.sql",
                "006_regression_releases.sql",
                "007_alerts.sql");

            await DatabaseMigrator.ApplyAsync(database.ConnectionString, earlierMigrations.FullName);

            // A project written before the filters existed.
            await TestDatabase.ExecuteAsync(
                database.ConnectionString,
                "insert into projects (name, slug) values ('Existing project', 'existing')");

            var applied = await DatabaseMigrator.ApplyAsync(
                database.ConnectionString,
                DatabaseMigrator.DefaultDirectory);

            Assert.Equal(["008_inbound_filters.sql"], applied);

            var columns = await TestDatabase.QueryStringsAsync(
                database.ConnectionString,
                """
                select column_name || '|' || data_type || '|' || udt_name || '|' || is_nullable || '|' || column_default
                from information_schema.columns
                where table_schema = 'public'
                  and table_name = 'projects'
                  and column_name like 'filter%'
                order by ordinal_position
                """);

            Assert.Equal(
                [
                    "filter_releases|ARRAY|_text|NO|'{}'::text[]",
                    "filter_environments|ARRAY|_text|NO|'{}'::text[]",
                    "filter_message_patterns|ARRAY|_text|NO|'{}'::text[]",
                ],
                columns);

            // Existing rows were backfilled by the column default.
            Assert.Equal(
                "0|0|0",
                Assert.Single(await TestDatabase.QueryStringsAsync(
                    database.ConnectionString,
                    """
                    select cardinality(filter_releases) || '|' ||
                           cardinality(filter_environments) || '|' ||
                           cardinality(filter_message_patterns)
                    from projects
                    where slug = 'existing'
                    """)));

            // New rows written without the columns get empty arrays as well.
            await TestDatabase.ExecuteAsync(
                database.ConnectionString,
                "insert into projects (name, slug) values ('New project', 'new')");

            Assert.Equal(
                "0|0|0",
                Assert.Single(await TestDatabase.QueryStringsAsync(
                    database.ConnectionString,
                    """
                    select cardinality(filter_releases) || '|' ||
                           cardinality(filter_environments) || '|' ||
                           cardinality(filter_message_patterns)
                    from projects
                    where slug = 'new'
                    """)));

            // The columns hold the stored globs verbatim.
            await TestDatabase.ExecuteAsync(
                database.ConnectionString,
                """
                update projects
                set filter_releases = array['1.2.*', '2.0.*'],
                    filter_environments = array['prod'],
                    filter_message_patterns = array['*timeout*']
                where slug = 'new'
                """);

            Assert.Equal(
                "1.2.*,2.0.*|prod|*timeout*",
                Assert.Single(await TestDatabase.QueryStringsAsync(
                    database.ConnectionString,
                    """
                    select array_to_string(filter_releases, ',') || '|' ||
                           array_to_string(filter_environments, ',') || '|' ||
                           array_to_string(filter_message_patterns, ',')
                    from projects
                    where slug = 'new'
                    """)));
        }
        finally
        {
            earlierMigrations.Delete(recursive: true);
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
}
