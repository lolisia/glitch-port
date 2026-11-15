using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GlitchPort.AspNetCore.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GlitchPort.AspNetCore.Tests;

public sealed class MiddlewareTests
{
    [Fact]
    public void EnvironmentFallsBackToTheHostEnvironmentName()
    {
        using var factory = new GlitchPortApplicationFactory();

        var client = factory.Services.GetRequiredService<GlitchPortClient>();

        Assert.Equal("Development", client.Options.Environment);
    }

    [Fact]
    public async Task CapturesAndRethrowsUnhandledException()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/boom");
        var body = await response.Content.ReadAsStringAsync();

        // The developer exception page is registered before UseGlitchPort, so it
        // must still handle the exception the middleware rethrew.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.Contains("boom", body, StringComparison.Ordinal);

        var events = await factory.FlushAndReadAsync();
        var captured = Assert.Single(events);

        Assert.Equal("error", captured["level"]?.GetValue<string>());
        Assert.Equal("GET", JsonAccess.GetString(captured, "tags", "http.method"));
        Assert.Equal("/boom", JsonAccess.GetString(captured, "tags", "http.route"));
        Assert.Equal("GET", JsonAccess.GetString(captured, "contexts", "request", "method"));
        Assert.Equal("/boom", JsonAccess.GetString(captured, "contexts", "request", "url"));
        Assert.Equal("/boom", JsonAccess.GetString(captured, "contexts", "request", "route"));

        var exceptions = Assert.IsType<JsonArray>(captured["exception"]);
        Assert.Equal(2, exceptions.Count);
        Assert.Equal("System.ArgumentException", exceptions[0]?["type"]?.GetValue<string>());
        Assert.Equal("inner", exceptions[0]?["value"]?.GetValue<string>());
        Assert.Equal("System.InvalidOperationException", exceptions[1]?["type"]?.GetValue<string>());
        Assert.Equal("boom", exceptions[1]?["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task RecordsRequestContextTagsAndAllowedHeadersOnly()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/boom-post?secret=value")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "GlitchPortTests/1.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-Secret", "must-not-leak");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        var captured = Assert.Single(events);

        Assert.Equal("POST", JsonAccess.GetString(captured, "contexts", "request", "method"));
        Assert.Equal("/boom-post", JsonAccess.GetString(captured, "contexts", "request", "url"));
        Assert.Equal("/boom-post", JsonAccess.GetString(captured, "contexts", "request", "route"));
        Assert.Equal("POST", JsonAccess.GetString(captured, "tags", "http.method"));
        Assert.Equal("/boom-post", JsonAccess.GetString(captured, "tags", "http.route"));

        var headers = Assert.IsType<JsonObject>(captured["contexts"]?["request"]?["headers"]);
        Assert.Equal(3, headers.Count);
        Assert.Equal("GlitchPortTests/1.0", headers["User-Agent"]?.GetValue<string>());
        Assert.Equal("application/json", headers["Accept"]?.GetValue<string>());
        Assert.StartsWith("application/json", headers["Content-Type"]?.GetValue<string>());
        Assert.False(headers.ContainsKey("X-Secret"));
        Assert.False(headers.ContainsKey("Host"));

        // SendDefaultPii defaults to false.
        Assert.False(captured.ContainsKey("user"));
        Assert.False(JsonAccess.Has(captured, "contexts", "request", "client_ip"));
    }

    [Fact]
    public async Task SuppressesUserAndIpUnlessSendDefaultPiiIsEnabled()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        using var request = CreatePiiRequest();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        var captured = Assert.Single(events);

        Assert.False(captured.ContainsKey("user"));
        Assert.False(JsonAccess.Has(captured, "contexts", "request", "client_ip"));
    }

    [Fact]
    public async Task RecordsUserAndIpWhenSendDefaultPiiIsEnabled()
    {
        using var factory = new GlitchPortApplicationFactory(sendDefaultPii: true);
        using var client = factory.CreateClient();

        using var request = CreatePiiRequest();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        var captured = Assert.Single(events);

        Assert.Equal("alice", JsonAccess.GetString(captured, "user", "username"));
        Assert.Equal("203.0.113.7", JsonAccess.GetString(captured, "user", "ip_address"));
        Assert.Equal("203.0.113.7", JsonAccess.GetString(captured, "contexts", "request", "client_ip"));
    }

    [Fact]
    public async Task LoggerAndMiddlewareReportTheSameExceptionOnlyOnce()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/log-error-and-throw");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        var captured = Assert.Single(events);

        Assert.Equal(Program.TestCategory, captured["logger"]?.GetValue<string>());
        Assert.Equal("logged error", captured["message"]?.GetValue<string>());
        Assert.Equal("error", captured["level"]?.GetValue<string>());
        Assert.Equal(2, Assert.IsType<JsonArray>(captured["exception"]).Count);
    }

    [Fact]
    public async Task LoggerSkipsAnExceptionAlreadyCapturedByTheMiddleware()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/outer-log");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        var captured = Assert.Single(events);

        // The middleware event wins; the outer logger call is skipped because
        // the exception instance is already marked.
        Assert.False(captured.ContainsKey("logger"));
        Assert.Equal("/outer-log", JsonAccess.GetString(captured, "contexts", "request", "route"));
        Assert.Equal(2, Assert.IsType<JsonArray>(captured["exception"]).Count);
    }

    [Fact]
    public async Task RepeatedLoggerCallsForOneExceptionInstanceReportItOnce()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/log-error-twice");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        var captured = Assert.Single(events);

        Assert.Equal("first log", captured["message"]?.GetValue<string>());
        Assert.Equal(Program.TestCategory, captured["logger"]?.GetValue<string>());
        Assert.IsType<JsonArray>(captured["exception"]);
    }

    [Fact]
    public async Task LoggerBreadcrumbsAreAttachedToTheRequestEvent()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/breadcrumb-then-error");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        var captured = Assert.Single(events);

        var breadcrumbs = GetBreadcrumbs(captured);
        var breadcrumb = Assert.Single(
            breadcrumbs,
            item => item["message"]?.GetValue<string>() == "breadcrumb message");

        Assert.Equal(Program.TestCategory, breadcrumb["category"]?.GetValue<string>());
        Assert.Equal("log", breadcrumb["type"]?.GetValue<string>());
        Assert.Equal("info", breadcrumb["level"]?.GetValue<string>());
    }

    [Fact]
    public async Task InformationLogsOnlyCreateBreadcrumbsByDefault()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/log-info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        Assert.Empty(events);
    }

    [Fact]
    public async Task MinimumEventLevelConfigurationCreatesEventsForWarnings()
    {
        using var factory = new GlitchPortApplicationFactory(settings =>
        {
            settings["GlitchPort:MinimumEventLevel"] = "Warning";
            settings["GlitchPort:MinimumBreadcrumbLevel"] = "Debug";
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/log-warning");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        var captured = Assert.Single(
            events,
            item => item["logger"]?.GetValue<string>() == Program.TestCategory);

        Assert.Equal("warning", captured["level"]?.GetValue<string>());
        Assert.Equal("warning message", captured["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task LogLevelsAreMappedToProtocolLevels()
    {
        using var factory = new GlitchPortApplicationFactory(settings =>
        {
            settings["GlitchPort:MinimumEventLevel"] = "Trace";
            settings["GlitchPort:MinimumBreadcrumbLevel"] = "Trace";
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/log-levels");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        var applicationEvents = events
            .Where(item => item["logger"]?.GetValue<string>() == Program.TestCategory)
            .Select(item => new
            {
                Level = item["level"]?.GetValue<string>(),
                Message = item["message"]?.GetValue<string>(),
            })
            .ToArray();

        Assert.Equal(6, applicationEvents.Length);

        Assert.Equal(
            new[] { "debug", "debug", "info", "warning", "error", "fatal" },
            applicationEvents.Select(item => item.Level).ToArray());

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
            applicationEvents.Select(item => item.Message).ToArray());
    }

    [Fact]
    public async Task CategoriesStartingWithGlitchPortAreIgnored()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/glitchport-category");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await factory.FlushAndReadAsync();
        Assert.DoesNotContain(
            events,
            item => item["logger"]?.GetValue<string>()?.StartsWith(
                "GlitchPort",
                StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task ParallelRequestsKeepTheirScopesSeparate()
    {
        using var factory = new GlitchPortApplicationFactory();
        using var client = factory.CreateClient();

        var first = client.GetAsync("/scope/first");
        var second = client.GetAsync("/scope/second");

        var responses = await Task.WhenAll(first, second);
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        var events = await factory.FlushAndReadAsync();
        Assert.Equal(2, events.Count);

        var firstEvent = Assert.Single(events, item => item["message"]?.GetValue<string>() == "event-first");
        var secondEvent = Assert.Single(events, item => item["message"]?.GetValue<string>() == "event-second");

        Assert.Equal("first", JsonAccess.GetString(firstEvent, "tags", "request.name"));
        Assert.Equal("second", JsonAccess.GetString(secondEvent, "tags", "request.name"));

        Assert.Equal("/scope/{name}", JsonAccess.GetString(firstEvent, "contexts", "request", "route"));
        Assert.Equal("/scope/{name}", JsonAccess.GetString(secondEvent, "contexts", "request", "route"));
        Assert.Equal("/scope/first", JsonAccess.GetString(firstEvent, "contexts", "request", "url"));
        Assert.Equal("/scope/second", JsonAccess.GetString(secondEvent, "contexts", "request", "url"));

        var firstBreadcrumbs = GetBreadcrumbs(firstEvent)
            .Select(item => item["message"]?.GetValue<string>())
            .ToArray();
        var secondBreadcrumbs = GetBreadcrumbs(secondEvent)
            .Select(item => item["message"]?.GetValue<string>())
            .ToArray();

        Assert.Contains("scope first", firstBreadcrumbs);
        Assert.DoesNotContain("scope second", firstBreadcrumbs);
        Assert.Contains("scope second", secondBreadcrumbs);
        Assert.DoesNotContain("scope first", secondBreadcrumbs);
    }

    private static HttpRequestMessage CreatePiiRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/boom");
        request.Headers.TryAddWithoutValidation(Program.UserHeader, "alice");
        request.Headers.TryAddWithoutValidation(Program.ClientIpHeader, "203.0.113.7");
        return request;
    }

    private static JsonObject[] GetBreadcrumbs(JsonObject capturedEvent)
    {
        return capturedEvent["breadcrumbs"] is JsonArray breadcrumbs
            ? breadcrumbs.OfType<JsonObject>().ToArray()
            : Array.Empty<JsonObject>();
    }
}
