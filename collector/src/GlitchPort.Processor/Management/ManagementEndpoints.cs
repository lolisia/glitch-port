using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Maps the management API under <c>/api/manage</c>.
/// </summary>
/// <remarks>
/// Every endpoint requires <c>Authorization: Bearer {Processor:AdminToken}</c>,
/// answers with snake_case JSON and UTC ISO 8601 timestamps, and reports
/// unknown ids with <c>404</c> and invalid values with <c>400</c>.
/// </remarks>
internal static class ManagementEndpoints
{
    /// <summary>
    /// Maps the project, key, issue, event, label, statistics, maintenance,
    /// alert, release and tag management endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup("/api/manage")
            .AddEndpointFilter<ManagementAuthorizationFilter>();

        // Release and tag reads are the only stores built here: they depend on
        // nothing but the data source (and the tag sample size), which are both
        // already in the container.
        var dataSource = endpoints.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        var tagSampleLimit = configuration.GetValue<int?>(
            ManagementTagEndpoints.SampleLimitConfigurationKey) ?? ManagementTagStore.DefaultSampleLimit;

        group.MapProjectEndpoints();
        group.MapIssueEndpoints();
        group.MapEventEndpoints();
        group.MapLabelEndpoints();
        group.MapStatsEndpoints();
        group.MapMaintenanceEndpoints();
        group.MapAlertEndpoints();
        group.MapReleaseEndpoints(new ManagementReleaseStore(dataSource));
        group.MapTagEndpoints(new ManagementTagStore(dataSource, tagSampleLimit));

        return endpoints;
    }
}
