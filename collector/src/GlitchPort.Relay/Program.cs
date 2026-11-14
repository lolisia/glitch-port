using GlitchPort.Collector.Contracts;
using GlitchPort.Relay;
using GlitchPort.Transport.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
    RelayOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<RelayOptions>();
    return new HttpClient { Timeout = options.Timeout };
});

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
    static () => Results.Json(
        new HealthResponse { Status = "ok" },
        RelayJsonContext.Default.HealthResponse));

app.MapPost("/api/{projectId:long}/events", RelayIngestEndpoints.HandleEventAsync);
app.MapPost("/api/{projectId:long}/messages", RelayIngestEndpoints.HandleMessageAsync);

app.Run();

public partial class Program { }
