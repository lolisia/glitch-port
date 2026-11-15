using System.Reflection;
using GlitchPort.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GlitchPort.AspNetCore.Tests.Infrastructure;

/// <summary>
/// Hosts the test entry point with a unique <c>file:</c> relay directory and the
/// settings a test wants to override.
/// </summary>
internal sealed class GlitchPortApplicationFactory : WebApplicationFactory<Program>
{
    public GlitchPortApplicationFactory(
        Action<IDictionary<string, string?>>? configure = null,
        bool sendDefaultPii = false)
    {
        RelayDirectory = new TempDirectory();
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GlitchPort:Dsn"] = TestRelay.CreateDsn(RelayDirectory.Path),
            ["GlitchPort:SendDefaultPii"] = sendDefaultPii ? "true" : "false",
        };

        configure?.Invoke(settings);
        TestApplicationSettings.Configure(settings);
    }

    public TempDirectory RelayDirectory { get; }

    protected override IEnumerable<Assembly> GetTestAssemblies()
    {
        // The entry point lives in this test assembly, so no other project
        // references it. Point the factory at our own assembly so the
        // WebApplicationFactoryContentRootAttribute (declared in the csproj) is
        // found without a solution file.
        return new[] { typeof(Program).Assembly };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The GlitchPort settings are applied to builder.Configuration by the
        // entry point before AddGlitchPort binds them (see TestApplicationSettings).
        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            RelayDirectory.Dispose();
        }
    }

    /// <summary>Flushes the singleton client and returns every event the relay received.</summary>
    public async Task<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> FlushAndReadAsync()
    {
        var client = Services.GetRequiredService<GlitchPortClient>();
        await client.FlushAsync(TimeSpan.FromSeconds(10));

        return TestRelay.Read(RelayDirectory.Path);
    }
}
