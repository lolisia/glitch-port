using GlitchPort.AspNetCore;

// Minimal host that shows the two things the ASP.NET Core integration does for
// a game server:
//
//   GET /ok        - always answers 200, so a readiness probe can wait for the
//                    host without touching the error paths.
//   GET /boom      - throws an InvalidOperationException with an inner
//                    ArgumentException. The exception bubbles through
//                    UseGlitchPort, which reports it as one event and rethrows
//                    it, so the developer exception page still answers 500.
//   GET /log-error - writes one ILogger error. The provider registered by
//                    AddGlitchPort forwards it as an event without an
//                    exception.
//
// Run it against the local collector with:
//
//   ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5070 \
//     dotnet run --project sdk/dotnet/samples/GameServerSample \
//     --no-build --no-launch-profile
//
// scripts/e2e-dotnet-server.sh starts PostgreSQL, the Relay, the Processor and
// this sample and asserts what the Processor stored.

var builder = WebApplication.CreateBuilder(args);

// Binds the "GlitchPort" section of appsettings.Development.json
// (GlitchPort:Dsn = http://dev-public-key@localhost:5080/1) and registers the
// client, the request middleware dependencies and the ILogger provider.
builder.Services.AddGlitchPort(builder.Configuration);

var app = builder.Build();

// Pushes one scope per request (so contexts.request ends up on every event of
// the request) and captures unhandled request exceptions at error level.
app.UseGlitchPort();

app.MapGet("/ok", () => Results.Ok(new { status = "ok" }));

app.MapGet("/boom", () =>
{
    // The protocol stores exceptions from the innermost cause to the outermost
    // one, so the ArgumentException comes first and this exception last.
    // scripts/e2e-dotnet-server.sh asserts exactly that order.
    throw new InvalidOperationException(
        SampleMessages.BoomMessage,
        new ArgumentException(SampleMessages.BoomInnerMessage));
});

app.MapGet("/log-error", (ILoggerFactory loggerFactory) =>
{
    loggerFactory
        .CreateLogger("GameServerSample")
        .LogError(SampleMessages.LogErrorMessage);

    return Results.Ok(new { status = "logged" });
});

app.Run();

/// <summary>
/// The messages the sample throws and logs. scripts/e2e-dotnet-server.sh looks
/// for the exact strings in the management API, so they live in one place.
/// </summary>
internal static class SampleMessages
{
    /// <summary>The message of the exception thrown by <c>GET /boom</c>.</summary>
    internal const string BoomMessage = "boom from GameServerSample";

    /// <summary>The message of the inner exception thrown by <c>GET /boom</c>.</summary>
    internal const string BoomInnerMessage = "inner argument";

    /// <summary>The message logged by <c>GET /log-error</c>.</summary>
    internal const string LogErrorMessage = "log error from GameServerSample";
}

/// <summary>
/// Entry point marker: a test host (or a reference from another project) needs
/// a public type to point at, which top-level statements alone do not provide.
/// </summary>
public partial class Program
{
}
