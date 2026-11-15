using Microsoft.Extensions.Logging;

namespace GlitchPort.AspNetCore;

/// <summary>
/// Options that control the ASP.NET Core integration and have no counterpart in
/// the transport-oriented <see cref="GlitchPortOptions"/>.
/// </summary>
/// <remarks>
/// The values are read from the <c>GlitchPort</c> configuration section by
/// <see cref="GlitchPortServiceCollectionExtensions.AddGlitchPort"/>. Logs at or
/// above <see cref="MinimumEventLevel"/> become captured events; logs below that
/// level but at or above <see cref="MinimumBreadcrumbLevel"/> become breadcrumbs
/// on the current scope.
/// </remarks>
public sealed class GlitchPortAspNetCoreOptions
{
    /// <summary>
    /// Gets or sets the minimum <see cref="LogLevel"/> that is recorded as a
    /// breadcrumb on the current scope. Defaults to
    /// <see cref="LogLevel.Information"/>.
    /// </summary>
    public LogLevel MinimumBreadcrumbLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Gets or sets the minimum <see cref="LogLevel"/> that creates a captured
    /// event. Defaults to <see cref="LogLevel.Error"/>.
    /// </summary>
    public LogLevel MinimumEventLevel { get; set; } = LogLevel.Error;
}
