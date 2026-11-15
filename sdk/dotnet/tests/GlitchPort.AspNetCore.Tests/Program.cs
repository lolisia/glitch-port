using System.Security.Claims;
using System.Text.Encodings.Web;
using GlitchPort;
using GlitchPort.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlitchPort.AspNetCore.Tests;

/// <summary>
/// The minimal web application hosted by <c>WebApplicationFactory</c>. It wires
/// glitch-port exactly like a real application would and exposes a handful of
/// endpoints that exercise the integration.
/// </summary>
public sealed class Program
{
    /// <summary>The category used by every endpoint logger.</summary>
    public const string TestCategory = "Test.Application";

    /// <summary>The test authentication scheme name.</summary>
    public const string AuthenticationScheme = "Test";

    /// <summary>Header that provides the authenticated user name.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>Header that provides a deterministic client IP address.</summary>
    public const string ClientIpHeader = "X-Test-Client-Ip";

    /// <summary>Process entry point used by <c>WebApplicationFactory</c>.</summary>
    public static void Main(string[] args)
    {
        BuildApplication(args).Run();
    }

    /// <summary>Builds the application; also callable from tests directly.</summary>
    public static WebApplication BuildApplication(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = WebApplication.CreateBuilder(args);

        // The factory's per-test settings must be visible before AddGlitchPort
        // binds the GlitchPort section.
        Infrastructure.TestApplicationSettings.Apply(builder);

        builder.Services
            .AddAuthentication(AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                AuthenticationScheme,
                configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddGlitchPort(builder.Configuration, options =>
        {
            // The callback runs last, so it can override bound configuration.
            options.SdkName = "glitchport.aspnetcore.tests";

            var configureRelease = builder.Configuration["GlitchPort:ConfigureRelease"];
            if (!string.IsNullOrEmpty(configureRelease))
            {
                options.Release = configureRelease;
            }
        });

        var app = builder.Build();

        app.UseMiddleware<RemoteAddressMiddleware>();

        // Registered before UseGlitchPort: the middleware rethrows, so the
        // developer exception page still turns the failure into a 500 response.
        app.UseDeveloperExceptionPage();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        // An outer logger that logs the exception after UseGlitchPort rethrows
        // it: the marker makes the logger skip the already captured instance.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/outer-log"))
            {
                await next(context);
                return;
            }

            try
            {
                await next(context);
            }
            catch (Exception exception)
            {
                context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(TestCategory)
                    .LogError(exception, "outer log");
                throw;
            }
        });

        app.UseGlitchPort();

        app.MapGet("/ok", () => "ok");
        app.MapGet("/boom", Boom);
        app.MapPost("/boom-post", Boom);
        app.MapGet("/outer-log", Boom);

        app.MapGet("/log-info", (ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger(TestCategory).LogInformation("info message");
            return "info";
        });

        app.MapGet("/log-warning", (ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger(TestCategory).LogWarning("warning message");
            return "warning";
        });

        app.MapGet("/log-error", (ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger(TestCategory).LogError("error message");
            return "error";
        });

        app.MapGet("/log-critical", (ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger(TestCategory).LogCritical("critical message");
            return "critical";
        });

        app.MapGet("/log-levels", (ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger(TestCategory);
            logger.LogTrace("trace message");
            logger.LogDebug("debug message");
            logger.LogInformation("information message");
            logger.LogWarning("warning message");
            logger.LogError("error message");
            logger.LogCritical("critical message");
            return "levels";
        });

        app.MapGet("/log-error-and-throw", (ILoggerFactory loggerFactory) =>
        {
            var exception = new InvalidOperationException("logged-and-thrown", new ArgumentException("inner"));
            loggerFactory.CreateLogger(TestCategory).LogError(exception, "logged error");
            throw exception;
        });

        app.MapGet("/log-error-twice", (ILoggerFactory loggerFactory) =>
        {
            var exception = new InvalidOperationException("logged-twice");
            var logger = loggerFactory.CreateLogger(TestCategory);
            logger.LogError(exception, "first log");
            logger.LogError(exception, "second log");
            return "twice";
        });

        app.MapGet("/glitchport-category", (ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger("glitchport.Internal").LogError("must be ignored");
            return "ignored";
        });

        app.MapGet("/breadcrumb-then-error", (ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger(TestCategory).LogInformation("breadcrumb message");
            throw new InvalidOperationException("after breadcrumb");
        });

        app.MapGet("/scope/{name}", async (string name, HttpContext context) =>
        {
            var client = context.RequestServices.GetRequiredService<GlitchPortClient>();
            client.SetTag("request.name", name);

            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(TestCategory)
                .LogInformation("scope {Name}", name);

            // Keep both requests in flight long enough to interleave their scopes.
            await Task.Delay(100);
            await client.CaptureMessageAsync("event-" + name);
            return name;
        });

        return app;
    }

    private static string Boom()
    {
        throw new InvalidOperationException("boom", new ArgumentException("inner"));
    }
}

/// <summary>
/// Turns the <see cref="Program.UserHeader"/> header into an authenticated
/// identity so the middleware can record <c>user.username</c>.
/// </summary>
internal sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Program.UserHeader, out var values)
            || string.IsNullOrEmpty(values.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[] { new Claim(ClaimTypes.Name, values.ToString()) };
        var identity = new ClaimsIdentity(claims, Program.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Program.AuthenticationScheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Gives the test server a deterministic remote address so the PII tests do not
/// depend on the hosting environment.
/// </summary>
internal sealed class RemoteAddressMiddleware
{
    private readonly RequestDelegate _next;

    public RemoteAddressMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(Program.ClientIpHeader, out var values)
            && System.Net.IPAddress.TryParse(values.ToString(), out var address))
        {
            context.Connection.RemoteIpAddress = address;
        }

        return _next(context);
    }
}
