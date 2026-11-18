using GlitchPort.Processor.Alerts;
using GlitchPort.Processor.Retention;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>Public Relay URL used to build DSNs.</summary>
    public const string DefaultPublicRelayUrl = "http://localhost:5080";

    private readonly string _connectionString;
    private readonly string _publicRelayUrl;
    private readonly HttpMessageHandler? _alertsHandler;

    /// <summary>
    /// Creates a factory against <paramref name="connectionString"/>. Tests
    /// that need a different DSN scheme override
    /// <paramref name="publicRelayUrl"/>; every other test uses the local
    /// development Relay URL. <paramref name="alertsHandler"/> replaces the
    /// primary handler of the named alerts client, so alert delivery tests can
    /// observe and answer HTTP requests without a real endpoint.
    /// </summary>
    public ProcessorApplicationFactory(
        string connectionString,
        string? publicRelayUrl = null,
        HttpMessageHandler? alertsHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _publicRelayUrl = string.IsNullOrWhiteSpace(publicRelayUrl) ? DefaultPublicRelayUrl : publicRelayUrl;
        _alertsHandler = alertsHandler;
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
                [$"{ProcessorOptions.SectionName}:PublicRelayUrl"] = _publicRelayUrl,
                [$"{ProcessorOptions.SectionName}:CorsOrigins:0"] = DashboardOrigin,

                // The hosted retention worker waits one full interval before
                // its first run; a day keeps it from ever firing during the
                // test suite. Retention itself is exercised directly.
                [$"{ProcessorOptions.SectionName}:{RetentionOptions.SectionName}:IntervalMinutes"] = "1440",

                // The alert workers wait one full interval too, and queueing is
                // exercised directly through the event store. Disabling them
                // keeps the shared host from delivering notifications while a
                // test is inspecting the queue; the dispatcher and evaluator
                // are constructed explicitly by their tests.
                [$"{ProcessorOptions.SectionName}:{AlertOptions.SectionName}:Enabled"] = "false",

                // The management API resolves a project before listing its
                // issues, so the shared test database needs the project id the
                // auth tests use. The bootstrap is idempotent.
                [$"{ProcessorOptions.SectionName}:Bootstrap:Projects:0:Id"] = "1",
                [$"{ProcessorOptions.SectionName}:Bootstrap:Projects:0:Name"] = "Default",
                [$"{ProcessorOptions.SectionName}:Bootstrap:Projects:0:Slug"] = "default",
                [$"{ProcessorOptions.SectionName}:Bootstrap:Projects:0:PublicKeys:0"] = "dev-public-key",
            });
        });

        if (_alertsHandler is not null)
        {
            builder.ConfigureServices(services =>
            {
                services
                    .AddHttpClient(AlertDispatcher.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _alertsHandler);
            });
        }
    }
}
