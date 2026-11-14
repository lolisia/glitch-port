using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Event statistics endpoint.
/// </summary>
internal static class ManagementStatsEndpoints
{
    private const string HourlyPeriod = "24h";
    private const string DailyPeriod = "14d";

    /// <summary>Maps the statistics endpoint on <paramref name="group"/>.</summary>
    public static RouteGroupBuilder MapStatsEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/projects/{projectId:long}/stats", GetStatsAsync);

        return group;
    }

    private static async Task<IResult> GetStatsAsync(
        long projectId,
        string? period,
        ManagementStatsStore stats,
        CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(period) ? HourlyPeriod : period.Trim().ToLowerInvariant();
        if (normalized is not (HourlyPeriod or DailyPeriod))
        {
            return ManagementHttp.BadRequest($"period must be {HourlyPeriod} or {DailyPeriod}.");
        }

        var buckets = await stats.GetAsync(projectId, normalized, cancellationToken).ConfigureAwait(false);

        return ManagementHttp.Json(buckets, ProcessorJsonContext.Default.ListStatsBucketResponse);
    }
}
