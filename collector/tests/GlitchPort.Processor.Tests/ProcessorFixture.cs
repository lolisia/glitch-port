using Testcontainers.PostgreSql;
using Xunit;

namespace GlitchPort.Processor.Tests;

[CollectionDefinition(Name)]
public sealed class ProcessorCollection : ICollectionFixture<ProcessorFixture>
{
    public const string Name = "Processor";
}

/// <summary>
/// Owns one PostgreSQL container and one running Processor instance shared by
/// every test in the collection.
/// </summary>
public sealed class ProcessorFixture : IAsyncLifetime
{
    /// <summary>
    /// First project id seeded for the tests, see <see cref="InitializeAsync"/>.
    /// </summary>
    public const long FirstSeededProjectId = 100000;

    /// <summary>
    /// Last project id seeded for the tests, see <see cref="InitializeAsync"/>.
    /// </summary>
    public const long LastSeededProjectId = 101000;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    private ProcessorApplicationFactory? _factory;

    public ProcessorApplicationFactory Factory =>
        _factory ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _factory = new ProcessorApplicationFactory(ConnectionString);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/healthz");
        response.EnsureSuccessStatusCode();

        // Issues, events and labels reference projects since migration 003,
        // but the Processor deliberately does not create projects while
        // ingesting. Seed the ids the tests draw from TestData.NextProjectId
        // so writes made directly by the tests stay valid.
        await TestDatabase.ExecuteAsync(
            ConnectionString,
            $"""
            insert into projects (id, name, slug)
            select id, 'Project ' || id, 'project-' || id
            from generate_series({FirstSeededProjectId}, {LastSeededProjectId}) as id
            on conflict do nothing;

            select setval(pg_get_serial_sequence('projects', 'id'), {LastSeededProjectId + 1}, false);
            """);
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _postgres.DisposeAsync();
    }

    public HttpClient CreateClient() => Factory.CreateClient();
}
