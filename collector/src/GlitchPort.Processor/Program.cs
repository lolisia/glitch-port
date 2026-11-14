using GlitchPort.Processor;
using GlitchPort.Processor.Data;
using GlitchPort.Processor.Endpoints;
using GlitchPort.Processor.Management;
using GlitchPort.Processor.Migrations;
using GlitchPort.Processor.Projects;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicyName = "dashboard";

builder.Services.Configure<ProcessorOptions>(builder.Configuration.GetSection(ProcessorOptions.SectionName));

// The policy is built from options so that the origins are read once the
// configuration (including test and environment overrides) is final.
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>()
    .Configure<IConfiguration>((corsOptions, configuration) =>
    {
        var origins = configuration
            .GetSection($"{ProcessorOptions.SectionName}:CorsOrigins")
            .Get<string[]>() ?? [];

        corsOptions.AddPolicy(CorsPolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod());
    });

builder.Services.AddSingleton(static services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

    return NpgsqlDataSource.Create(connectionString);
});
builder.Services.AddSingleton<MessageStore>();
builder.Services.AddSingleton<EventStore>();
builder.Services.AddSingleton<ManagementEventStore>();
builder.Services.AddSingleton<ManagementIssueStore>();
builder.Services.AddSingleton<ManagementLabelStore>();
builder.Services.AddSingleton<ManagementStatsStore>();
builder.Services.AddSingleton<PublicKeyGenerator>();
builder.Services.AddSingleton<ProjectStore>();

var app = builder.Build();

app.UseCors(CorsPolicyName);

app.MapGet("/healthz", () => Results.Ok());
app.MapEnvelopeEndpoints();
app.MapMessageEndpoints();
app.MapManagementEndpoints();

// The schema must be ready before the server accepts its first request.
await DatabaseMigrator.ApplyAsync(
    app.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."),
    logger: app.Logger,
    cancellationToken: app.Lifetime.ApplicationStopping).ConfigureAwait(false);

// Configured projects (development uses project 1 with the well-known
// dev-public-key) are created exactly once and only when they are missing.
await ProjectBootstrapper.BootstrapAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(),
    app.Services.GetRequiredService<IOptions<ProcessorOptions>>().Value.Bootstrap.Projects,
    logger: app.Logger,
    cancellationToken: app.Lifetime.ApplicationStopping).ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point marker used by integration tests.</summary>
public partial class Program
{
}
