using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GlitchPort.Relay.Tests;

public sealed class DevelopmentConfigurationTests
{
    [Fact]
    public void DevelopmentSettings_ConfigureProjectAndProcessor()
    {
        using var factory = CreateDevelopmentFactory();

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("http://localhost:5080", configuration["Urls"]);
        Assert.Equal("dev-public-key", configuration["Relay:Projects:1:0"]);
        Assert.Equal("http://localhost:5090", configuration["Relay:ProcessorEndpoints:0"]);
        Assert.Equal("dev-internal-secret", configuration["Relay:InternalSharedSecret"]);

        var options = factory.Services.GetRequiredService<RelayOptions>();
        Assert.Equal("http://localhost:5090", Assert.Single(options.ProcessorEndpoints));
        Assert.Equal("dev-internal-secret", options.InternalSharedSecret);
        Assert.True(options.Projects.TryGetValue(1, out var projectKeys));
        Assert.Equal("dev-public-key", Assert.Single(projectKeys));
    }

    [Fact]
    public void LaunchSettings_ExposeHttpProfileOnPort5080()
    {
        using var factory = CreateDevelopmentFactory();

        var contentRoot = factory.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
        var launchSettingsPath = Path.Combine(contentRoot, "Properties", "launchSettings.json");
        Assert.True(
            File.Exists(launchSettingsPath),
            $"launchSettings.json was not found at '{launchSettingsPath}'.");

        using var document = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
        var httpProfile = document.RootElement.GetProperty("profiles").GetProperty("http");
        Assert.Equal("http://localhost:5080", httpProfile.GetProperty("applicationUrl").GetString());
    }

    private static WebApplicationFactory<Program> CreateDevelopmentFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
}
