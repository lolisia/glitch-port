using System.Globalization;
using GlitchPort.Collector.Contracts;
using GlitchPort.Relay;
using GlitchPort.Relay.Config;
using GlitchPort.Relay.RateLimit;
using GlitchPort.Transport.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
    RelayOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<RelayOptions>();
    return new HttpClient { Timeout = options.Timeout };
});

// The project-config client uses its own HttpClient, configured through the
// factory so tests can replace the primary handler without affecting the
// envelope publisher.
builder.Services.AddHttpClient(ProjectConfigClient.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<RelayOptions>();
    client.Timeout = options.Timeout;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProjectConfigProvider>();
builder.Services.AddSingleton(sp => new ProjectRateLimiter(
    sp.GetRequiredService<RelayOptions>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new ProjectConfigClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(ProjectConfigClient.HttpClientName),
    sp.GetRequiredService<ILogger<ProjectConfigClient>>()));
builder.Services.AddSingleton<ProjectConfigCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProjectConfigCache>());

// Registered as a singleton so tests can swap the publisher through
// ConfigureTestServices without touching the transport implementation.
builder.Services.AddSingleton<IEnvelopePublisher>(sp =>
{
    var options = sp.GetRequiredService<RelayOptions>();
    return new HttpEnvelopePublisher(
        new HttpTransportOptions
        {
            Endpoints = options.ProcessorEndpoints,
            SharedSecret = options.InternalSharedSecret,
            Timeout = options.Timeout,
        },
        sp.GetRequiredService<HttpClient>());
});

var app = builder.Build();

app.MapGet(
    "/healthz",
    static (ProjectConfigProvider projectConfigs) =>
    {
        var status = projectConfigs.GetStatus();

        return Results.Json(
            new HealthResponse
            {
                Status = "ok",
                Config = new HealthConfigResponse
                {
                    Source = status.Source,
                    LastRefresh = status.LastRefresh?.UtcDateTime.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    ProjectCount = status.ProjectCount,
                },
            },
            RelayJsonContext.Default.HealthResponse);
    });

app.MapPost("/api/{projectId:long}/events", RelayIngestEndpoints.HandleEventAsync);
app.MapPost("/api/{projectId:long}/messages", RelayIngestEndpoints.HandleMessageAsync);

app.Run();

public partial class Program { }
