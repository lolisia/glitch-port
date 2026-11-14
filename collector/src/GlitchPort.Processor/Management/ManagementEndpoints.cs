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
    /// Maps the issue, event, label and statistics management endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup("/api/manage")
            .AddEndpointFilter<ManagementAuthorizationFilter>();

        group.MapIssueEndpoints();
        group.MapEventEndpoints();
        group.MapLabelEndpoints();
        group.MapStatsEndpoints();

        return endpoints;
    }
}
