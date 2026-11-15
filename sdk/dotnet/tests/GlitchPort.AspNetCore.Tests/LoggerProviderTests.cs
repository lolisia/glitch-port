using System.Text.Json.Nodes;
using GlitchPort.AspNetCore.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GlitchPort.AspNetCore.Tests;

public sealed class LoggerProviderTests
{
    [Fact]
    public async Task LogLevelsAreMappedToProtocolLevels()
    {
        using var temp = new TempDirectory();
        using var provider = TestServiceProvider.Create(
            temp.Path,
            new Dictionary<string, string?>
            {
                ["GlitchPort:MinimumEventLevel"] = "Trace",
                ["GlitchPort:MinimumBreadcrumbLevel"] = "Trace",
            });

        var logger = provider.CreateLogger("Test.Mapping");
        logger.LogTrace("trace message");
        logger.LogDebug("debug message");
        logger.LogInformation("information message");
        logger.LogWarning("warning message");
        logger.LogError("error message");
        logger.LogCritical("critical message");

        var events = await FlushAsync(provider, temp.Path);

        Assert.Equal(
            new[] { "debug", "debug", "info", "warning", "error", "fatal" },
            events.Select(item => item["level"]?.GetValue<string>()).ToArray());

        Assert.Equal(
            new[]
            {
                "trace message",
                "debug message",
                "information message",
                "warning message",
                "error message",
                "critical message",
            },
            events.Select(item => item["message"]?.GetValue<string>()).ToArray());

        Assert.All(
            events,
            item => Assert.Equal("Test.Mapping", item["logger"]?.GetValue<string>()));
    }

    [Fact]
    public async Task DefaultThresholdsCreateBreadcrumbsForInformationAndEventsForErrors()
    {
        using var temp = new TempDirectory();
        using var provider = TestServiceProvider.Create(temp.Path);
        var client = provider.GetRequiredService<GlitchPortClient>();
        var logger = provider.CreateLogger("Test.Defaults");

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Error));

        using (client.PushScope())
        {
            logger.LogInformation("info breadcrumb");
            logger.LogWarning("warning breadcrumb");
            await client.CaptureMessageAsync("carrier");
        }

        logger.LogError("error event");

        var events = await FlushAsync(provider, temp.Path);
        Assert.Equal(2, events.Count);

        var carrier = Assert.Single(events, item => item["message"]?.GetValue<string>() == "carrier");
        var breadcrumbs = carrier["breadcrumbs"] is JsonArray array
            ? array.OfType<JsonObject>().ToArray()
            : Array.Empty<JsonObject>();

        Assert.Equal(2, breadcrumbs.Length);
        Assert.Equal("info breadcrumb", breadcrumbs[0]["message"]?.GetValue<string>());
        Assert.Equal("warning breadcrumb", breadcrumbs[1]["message"]?.GetValue<string>());
        Assert.All(
            breadcrumbs,
            item =>
            {
                Assert.Equal("Test.Defaults", item["category"]?.GetValue<string>());
                Assert.Equal("log", item["type"]?.GetValue<string>());
            });
        Assert.Equal("info", breadcrumbs[0]["level"]?.GetValue<string>());
        Assert.Equal("warning", breadcrumbs[1]["level"]?.GetValue<string>());

        var errorEvent = Assert.Single(events, item => item["message"]?.GetValue<string>() == "error event");
        Assert.Equal("Test.Defaults", errorEvent["logger"]?.GetValue<string>());
        Assert.Equal("error", errorEvent["level"]?.GetValue<string>());
    }

    [Fact]
    public async Task LogsBelowTheBreadcrumbThresholdAreDropped()
    {
        using var temp = new TempDirectory();
        using var provider = TestServiceProvider.Create(
            temp.Path,
            new Dictionary<string, string?>
            {
                ["GlitchPort:MinimumBreadcrumbLevel"] = "Warning",
                ["GlitchPort:MinimumEventLevel"] = "Error",
            });

        var client = provider.GetRequiredService<GlitchPortClient>();
        var logger = provider.CreateLogger("Test.Threshold");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));

        using (client.PushScope())
        {
            logger.LogInformation("dropped breadcrumb");
            logger.LogWarning("kept breadcrumb");
            await client.CaptureMessageAsync("carrier");
        }

        var events = await FlushAsync(provider, temp.Path);
        var carrier = Assert.Single(events);

        var breadcrumbs = carrier["breadcrumbs"] is JsonArray array
            ? array.OfType<JsonObject>().ToArray()
            : Array.Empty<JsonObject>();

        Assert.Equal("kept breadcrumb", Assert.Single(breadcrumbs)["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task MinimumEventLevelSuppressesErrorEvents()
    {
        using var temp = new TempDirectory();
        using var provider = TestServiceProvider.Create(
            temp.Path,
            new Dictionary<string, string?>
            {
                ["GlitchPort:MinimumEventLevel"] = "Critical",
                ["GlitchPort:MinimumBreadcrumbLevel"] = "Information",
            });

        var logger = provider.CreateLogger("Test.Critical");

        logger.LogError("error message");
        logger.LogCritical("critical message");

        var events = await FlushAsync(provider, temp.Path);
        var captured = Assert.Single(events);

        Assert.Equal("critical message", captured["message"]?.GetValue<string>());
        Assert.Equal("fatal", captured["level"]?.GetValue<string>());
    }

    [Fact]
    public async Task CategoriesStartingWithGlitchPortAreIgnored()
    {
        using var temp = new TempDirectory();
        using var provider = TestServiceProvider.Create(temp.Path);
        var logger = provider.CreateLogger("glitchport.Internal");

        Assert.False(logger.IsEnabled(LogLevel.Critical));

        logger.LogCritical(new InvalidOperationException("ignored"), "ignored with exception");
        logger.LogError("ignored without exception");

        var events = await FlushAsync(provider, temp.Path);
        Assert.Empty(events);
    }

    [Fact]
    public async Task SameExceptionInstanceIsCapturedAtMostOnce()
    {
        using var temp = new TempDirectory();
        using var provider = TestServiceProvider.Create(temp.Path);
        var logger = provider.CreateLogger("Test.Duplicates");

        var exception = new InvalidOperationException("first exception");
        logger.LogError(exception, "first log");
        logger.LogError(exception, "second log");
        logger.LogError(new InvalidOperationException("other exception"), "other log");

        var events = await FlushAsync(provider, temp.Path);
        Assert.Equal(2, events.Count);

        var firstEvent = Assert.Single(events, item => item["message"]?.GetValue<string>() == "first log");
        Assert.Equal("first exception", firstEvent["exception"]?[0]?["value"]?.GetValue<string>());

        var otherEvent = Assert.Single(events, item => item["message"]?.GetValue<string>() == "other log");
        Assert.Equal("other exception", otherEvent["exception"]?[0]?["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExceptionsAreParsedWithTheClientOptions()
    {
        using var temp = new TempDirectory();
        using var provider = TestServiceProvider.Create(temp.Path);
        var logger = provider.CreateLogger("Test.Exceptions");

        logger.LogError(
            new InvalidOperationException("outer", new ArgumentException("inner")),
            "boom");

        var events = await FlushAsync(provider, temp.Path);
        var captured = Assert.Single(events);

        Assert.Equal("boom", captured["message"]?.GetValue<string>());
        Assert.Equal("Test.Exceptions", captured["logger"]?.GetValue<string>());

        var exceptions = Assert.IsType<JsonArray>(captured["exception"]);
        Assert.Equal(2, exceptions.Count);
        Assert.Equal("System.ArgumentException", exceptions[0]?["type"]?.GetValue<string>());
        Assert.Equal("inner", exceptions[0]?["value"]?.GetValue<string>());
        Assert.Equal("System.InvalidOperationException", exceptions[1]?["type"]?.GetValue<string>());
        Assert.Equal("outer", exceptions[1]?["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task EventMessageComesFromTheFormatter()
    {
        using var temp = new TempDirectory();
        using var provider = TestServiceProvider.Create(temp.Path);
        var logger = provider.CreateLogger("Test.Formatting");

        logger.Log(
            LogLevel.Error,
            new EventId(42, "boom"),
            "state-value",
            new InvalidOperationException("exception-value"),
            (state, exception) => "formatted " + state + " (" + exception?.GetType().Name + ")");

        var events = await FlushAsync(provider, temp.Path);
        var captured = Assert.Single(events);

        Assert.Equal(
            "formatted state-value (InvalidOperationException)",
            captured["message"]?.GetValue<string>());
        Assert.Equal("exception-value", captured["exception"]?[0]?["value"]?.GetValue<string>());
    }

    private static async Task<IReadOnlyList<JsonObject>> FlushAsync(ServiceProvider provider, string directory)
    {
        var client = provider.GetRequiredService<GlitchPortClient>();
        await client.FlushAsync(TimeSpan.FromSeconds(10));

        return TestRelay.Read(directory);
    }
}
