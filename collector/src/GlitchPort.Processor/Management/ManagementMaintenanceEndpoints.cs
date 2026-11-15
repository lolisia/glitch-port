using GlitchPort.Processor.Retention;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Maintenance endpoints: the configured retention window together with the
/// counters of the newest executed retention run.
/// </summary>
internal static class ManagementMaintenanceEndpoints
{
    /// <summary>Maps the maintenance endpoints on <paramref name="group"/>.</summary>
    public static RouteGroupBuilder MapMaintenanceEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/maintenance/retention", GetRetentionAsync);

        return group;
    }

    private static async Task<IResult> GetRetentionAsync(
        ManagementMaintenanceStore store,
        IOptions<RetentionOptions> options,
        CancellationToken cancellationToken)
    {
        var lastRun = await store.GetLastRetentionRunAsync(cancellationToken).ConfigureAwait(false);

        var response = new RetentionStatusResponse(
            options.Value.Days,
            lastRun?.FinishedAt,
            lastRun?.DeletedEvents ?? 0,
            lastRun?.DeletedIssues ?? 0);

        return ManagementHttp.Json(response, ProcessorJsonContext.Default.RetentionStatusResponse);
    }
}
