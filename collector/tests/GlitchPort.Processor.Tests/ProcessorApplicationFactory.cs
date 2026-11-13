using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace GlitchPort.Processor.Tests;

/// <summary>
/// Hosts the Processor in-process against a test PostgreSQL instance.
/// </summary>
public sealed class ProcessorApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Admin token used by the management API tests.</summary>
    public const string AdminToken = "test-admin-token";

    /// <summary>Internal shared secret used by the Relay-facing endpoint tests.</summary>
    public const string InternalSharedSecret = "test-internal-secret";

    /// <summary>Dashboard origin configured for CORS.</summary>
    public const string DashboardOrigin = "http://localhost:5173";

    private readonly string _connectionString;

    public ProcessorApplicationFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _connectionString,
                [$"{ProcessorOptions.SectionName}:AdminToken"] = AdminToken,
                [$"{ProcessorOptions.SectionName}:InternalSharedSecret"] = InternalSharedSecret,
                [$"{ProcessorOptions.SectionName}:CorsOrigins:0"] = DashboardOrigin,
            });
        });
    }
}
