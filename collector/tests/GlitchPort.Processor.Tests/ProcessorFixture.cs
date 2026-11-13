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
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _postgres.DisposeAsync();
    }

    public HttpClient CreateClient() => Factory.CreateClient();
}
