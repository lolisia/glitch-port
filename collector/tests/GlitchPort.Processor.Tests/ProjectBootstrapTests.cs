using GlitchPort.Processor.Migrations;
using GlitchPort.Processor.Projects;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace GlitchPort.Processor.Tests;

[Collection(ProcessorCollection.Name)]
public sealed class ProjectBootstrapTests
{
    private static readonly ProjectBootstrapProject[] DevelopmentProjects =
    [
        new()
        {
            Id = 1,
            Name = "Default",
            Slug = "default",
            PublicKeys = ["dev-public-key"],
        },
    ];

    private readonly ProcessorFixture _fixture;

    public ProjectBootstrapTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Running_the_bootstrap_twice_creates_the_project_and_key_once()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        await DatabaseMigrator.ApplyAsync(database.ConnectionString, DatabaseMigrator.DefaultDirectory);

        using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);

        await ProjectBootstrapper.BootstrapAsync(dataSource, DevelopmentProjects);
        await ProjectBootstrapper.BootstrapAsync(dataSource, DevelopmentProjects);

        Assert.Equal(1, await ScalarAsync(database.ConnectionString, "select count(*) from projects"));
        Assert.Equal(1, await ScalarAsync(database.ConnectionString, "select count(*) from project_keys"));
        Assert.Equal(
            1,
            await ScalarAsync(
                database.ConnectionString,
                "select count(*) from projects where id = 1 and name = 'Default' and slug = 'default'"));
        Assert.Equal(
            1,
            await ScalarAsync(
                database.ConnectionString,
                """
                select count(*)
                from project_keys
                where project_id = 1 and public_key = 'dev-public-key' and is_active and revoked_at is null
                """));

        var nextProjectId = await ScalarAsync(
            database.ConnectionString,
            "select nextval(pg_get_serial_sequence('projects', 'id'))");

        Assert.True(nextProjectId > 1);
    }

    [Fact]
    public async Task Concurrent_bootstraps_create_the_project_and_key_once()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        await DatabaseMigrator.ApplyAsync(database.ConnectionString, DatabaseMigrator.DefaultDirectory);

        using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);

        await Task.WhenAll(
            ProjectBootstrapper.BootstrapAsync(dataSource, DevelopmentProjects),
            ProjectBootstrapper.BootstrapAsync(dataSource, DevelopmentProjects));

        Assert.Equal(1, await ScalarAsync(database.ConnectionString, "select count(*) from projects"));
        Assert.Equal(1, await ScalarAsync(database.ConnectionString, "select count(*) from project_keys"));
    }

    [Fact]
    public void The_bootstrap_lock_differs_from_the_migration_lock()
    {
        Assert.NotEqual(DatabaseMigrator.AdvisoryLockKey, ProjectBootstrapper.AdvisoryLockKey);
    }

    [Fact]
    public void Options_bind_the_bootstrap_projects_and_the_public_relay_url()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ProcessorOptions.SectionName}:PublicRelayUrl"] = "http://localhost:5080",
                [$"{ProcessorOptions.SectionName}:Bootstrap:Projects:0:Id"] = "1",
                [$"{ProcessorOptions.SectionName}:Bootstrap:Projects:0:Name"] = "Default",
                [$"{ProcessorOptions.SectionName}:Bootstrap:Projects:0:Slug"] = "default",
                [$"{ProcessorOptions.SectionName}:Bootstrap:Projects:0:PublicKeys:0"] = "dev-public-key",
            })
            .Build();

        var options = new ProcessorOptions();
        configuration.GetSection(ProcessorOptions.SectionName).Bind(options);

        Assert.Equal("http://localhost:5080", options.PublicRelayUrl);

        var project = Assert.Single(options.Bootstrap.Projects);
        Assert.Equal(1, project.Id);
        Assert.Equal("Default", project.Name);
        Assert.Equal("default", project.Slug);
        Assert.Equal(["dev-public-key"], project.PublicKeys);

        Assert.Equal(
            "http://dev-public-key@localhost:5080/1",
            DsnBuilder.Build(options.PublicRelayUrl, project.PublicKeys[0], project.Id));
    }

    private static Task<long> ScalarAsync(string connectionString, string sql) =>
        TestDatabase.QuerySingleLongAsync(connectionString, sql);
}

/// <summary>
/// Covers the projects layer: <see cref="ProjectStore"/>,
/// <see cref="PublicKeyGenerator"/> and <see cref="DsnBuilder"/>.
/// </summary>
/// <remarks>
/// These tests live in this file because the task scope only allows new test
/// code in the migration and bootstrap test files.
/// </remarks>
[Collection(ProcessorCollection.Name)]
public sealed class ProjectStoreTests
{
    private readonly ProcessorFixture _fixture;

    public ProjectStoreTests(ProcessorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Public_key_generator_returns_32_lowercase_hex_characters()
    {
        var generator = new PublicKeyGenerator();

        var keys = Enumerable.Range(0, 32).Select(_ => generator.Generate()).ToList();

        Assert.Equal(32, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key =>
        {
            Assert.Equal(PublicKeyGenerator.PublicKeyLength, key.Length);
            Assert.All(key, character => Assert.True(
                character is >= '0' and <= '9' or >= 'a' and <= 'f',
                $"'{character}' is not a lowercase hex character."));
        });
    }

    [Theory]
    [InlineData("http://localhost:5080", "dev-public-key", 1L, "http://dev-public-key@localhost:5080/1")]
    [InlineData("http://localhost:5080/", "dev-public-key", 7L, "http://dev-public-key@localhost:5080/7")]
    [InlineData("https://relay.example.com", "abc123", 42L, "https://abc123@relay.example.com/42")]
    [InlineData("https://relay.example.com:8443", "abc123", 42L, "https://abc123@relay.example.com:8443/42")]
    public void Dsn_builder_builds_the_project_dsn(
        string publicRelayUrl,
        string publicKey,
        long projectId,
        string expected)
    {
        Assert.Equal(expected, DsnBuilder.Build(publicRelayUrl, publicKey, projectId));
    }

    [Theory]
    [InlineData("ftp://relay.example.com")]
    [InlineData("relay.example.com:5080")]
    public void Dsn_builder_rejects_urls_that_are_not_http(string publicRelayUrl)
    {
        Assert.ThrowsAny<ArgumentException>(() => DsnBuilder.Build(publicRelayUrl, "key", 1));
    }

    [Fact]
    public async Task Create_writes_the_project_and_its_first_active_key()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        await DatabaseMigrator.ApplyAsync(database.ConnectionString, DatabaseMigrator.DefaultDirectory);

        using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new ProjectStore(dataSource, new PublicKeyGenerator());

        var created = await store.CreateAsync("Star Raiders", "star-raiders", CancellationToken.None);

        Assert.NotNull(created);
        Assert.True(created.Project.Id > 0);
        Assert.Equal("Star Raiders", created.Project.Name);
        Assert.Equal("star-raiders", created.Project.Slug);
        Assert.Equal(created.Project.Id, created.Key.ProjectId);
        Assert.True(created.Key.IsActive);
        Assert.Null(created.Key.Label);
        Assert.Null(created.Key.RateLimitPerMinute);
        Assert.Null(created.Key.RevokedAt);
        Assert.Matches("^[0-9a-f]{32}$", created.Key.PublicKey);

        Assert.Equal(created.Project, await store.GetAsync(created.Project.Id, CancellationToken.None));
        Assert.Null(await store.GetAsync(created.Project.Id + 1, CancellationToken.None));
    }

    [Fact]
    public async Task Create_returns_null_when_the_slug_is_taken()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        await DatabaseMigrator.ApplyAsync(database.ConnectionString, DatabaseMigrator.DefaultDirectory);

        using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new ProjectStore(dataSource, new PublicKeyGenerator());

        var first = await store.CreateAsync("First", "taken", CancellationToken.None);
        Assert.NotNull(first);

        Assert.Null(await store.CreateAsync("Second", "taken", CancellationToken.None));

        var projects = await store.ListAsync(CancellationToken.None);
        Assert.Equal([first.Project.Id], projects.Select(project => project.Id));

        var keys = await store.ListKeysAsync(first.Project.Id, CancellationToken.None);
        Assert.Equal([first.Key.Id], keys.Select(key => key.Id));
    }

    [Fact]
    public async Task List_reports_unresolved_issues_and_active_keys()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        await DatabaseMigrator.ApplyAsync(database.ConnectionString, DatabaseMigrator.DefaultDirectory);

        using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new ProjectStore(dataSource, new PublicKeyGenerator());

        var alpha = await store.CreateAsync("Alpha", "alpha", CancellationToken.None);
        var beta = await store.CreateAsync("Beta", "beta", CancellationToken.None);
        Assert.NotNull(alpha);
        Assert.NotNull(beta);

        var revokedKey = await store.CreateKeyAsync(alpha.Project.Id, "ci", null, CancellationToken.None);
        Assert.NotNull(revokedKey);
        Assert.NotNull(await store.RevokeKeyAsync(alpha.Project.Id, revokedKey.Id, CancellationToken.None));

        // Beta loses the key its create added, so only Alpha has an active one.
        Assert.NotNull(await store.RevokeKeyAsync(beta.Project.Id, beta.Key.Id, CancellationToken.None));

        await TestDatabase.ExecuteAsync(
            database.ConnectionString,
            $"""
            insert into issues (project_id, fingerprint, title, level, platform, status, first_seen, last_seen)
            values ({alpha.Project.Id}, 'alpha-unresolved', 'boom', 'error', 'unity', 'unresolved', now(), now()),
                   ({alpha.Project.Id}, 'alpha-resolved', 'boom', 'error', 'unity', 'resolved', now(), now()),
                   ({beta.Project.Id}, 'beta-unresolved', 'boom', 'error', 'unity', 'unresolved', now(), now())
            """);

        var projects = await store.ListAsync(CancellationToken.None);

        Assert.Equal([alpha.Project.Id, beta.Project.Id], projects.Select(project => project.Id));
        Assert.Equal(1, projects[0].UnresolvedIssueCount);
        Assert.Equal(1, projects[0].ActiveKeyCount);
        Assert.Equal(1, projects[1].UnresolvedIssueCount);
        Assert.Equal(0, projects[1].ActiveKeyCount);
        Assert.Equal(["unity"], projects[0].Platforms);
        Assert.Equal(["unity"], projects[1].Platforms);
    }

    [Fact]
    public async Task Update_and_delete_change_the_project_row()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        await DatabaseMigrator.ApplyAsync(database.ConnectionString, DatabaseMigrator.DefaultDirectory);

        using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new ProjectStore(dataSource, new PublicKeyGenerator());

        var created = await store.CreateAsync("Item", "item", CancellationToken.None);
        Assert.NotNull(created);

        var updated = await store.UpdateAsync(created.Project.Id, "Renamed", CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("item", updated.Slug);

        Assert.Null(await store.UpdateAsync(created.Project.Id + 1, "Renamed", CancellationToken.None));

        Assert.True(await store.DeleteAsync(created.Project.Id, CancellationToken.None));
        Assert.False(await store.DeleteAsync(created.Project.Id, CancellationToken.None));
        Assert.Null(await store.GetAsync(created.Project.Id, CancellationToken.None));
        Assert.Empty(await store.ListKeysAsync(created.Project.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Keys_are_listed_ordered_and_can_be_updated_and_revoked()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        await DatabaseMigrator.ApplyAsync(database.ConnectionString, DatabaseMigrator.DefaultDirectory);

        using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new ProjectStore(dataSource, new PublicKeyGenerator());

        var project = await store.CreateAsync("Keys", "keys", CancellationToken.None);
        Assert.NotNull(project);

        var second = await store.CreateKeyAsync(project.Project.Id, "ci", 30, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("ci", second.Label);
        Assert.Equal(30, second.RateLimitPerMinute);
        Assert.True(second.IsActive);

        var keys = await store.ListKeysAsync(project.Project.Id, CancellationToken.None);
        Assert.Equal([project.Key.Id, second.Id], keys.Select(key => key.Id));

        var updated = await store.UpdateKeyAsync(
            project.Project.Id,
            second.Id,
            "nightly",
            5,
            false,
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("nightly", updated.Label);
        Assert.Equal(5, updated.RateLimitPerMinute);
        Assert.False(updated.IsActive);
        Assert.Null(updated.RevokedAt);

        var revoked = await store.RevokeKeyAsync(project.Project.Id, second.Id, CancellationToken.None);

        Assert.NotNull(revoked);
        Assert.False(revoked.IsActive);
        Assert.NotNull(revoked.RevokedAt);

        // The revoked key keeps its row.
        var allKeys = await store.ListKeysAsync(project.Project.Id, CancellationToken.None);
        Assert.Equal([project.Key.Id, second.Id], allKeys.Select(key => key.Id));

        Assert.Null(await store.UpdateKeyAsync(
            project.Project.Id,
            second.Id + 1_000,
            null,
            null,
            null,
            CancellationToken.None));
        Assert.Null(await store.RevokeKeyAsync(project.Project.Id, second.Id + 1_000, CancellationToken.None));
        Assert.Null(await store.CreateKeyAsync(project.Project.Id + 1_000, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Configs_contain_only_active_keys_ordered_by_id()
    {
        await using var database = await ScratchDatabase.CreateAsync(_fixture.ConnectionString);
        await DatabaseMigrator.ApplyAsync(database.ConnectionString, DatabaseMigrator.DefaultDirectory);

        using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new ProjectStore(dataSource, new PublicKeyGenerator());

        var alpha = await store.CreateAsync("Alpha", "alpha", CancellationToken.None);
        var beta = await store.CreateAsync("Beta", "beta", CancellationToken.None);
        Assert.NotNull(alpha);
        Assert.NotNull(beta);

        var revokedKey = await store.CreateKeyAsync(alpha.Project.Id, "old", null, CancellationToken.None);
        Assert.NotNull(revokedKey);
        Assert.NotNull(await store.RevokeKeyAsync(alpha.Project.Id, revokedKey.Id, CancellationToken.None));

        var configs = await store.ListConfigsAsync(CancellationToken.None);

        Assert.Equal([alpha.Project.Id, beta.Project.Id], configs.Select(config => config.Id));
        Assert.Equal("alpha", configs[0].Slug);
        Assert.Equal([alpha.Key.Id], configs[0].Keys.Select(key => key.Id));
        Assert.Equal(alpha.Key.PublicKey, configs[0].Keys[0].PublicKey);
        Assert.Equal([beta.Key.Id], configs[1].Keys.Select(key => key.Id));
    }
}
