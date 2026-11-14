using GlitchPort.Processor.Security;
using Microsoft.Extensions.Options;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Requires <c>Authorization: Bearer {Processor:AdminToken}</c> on every
/// management endpoint and answers <c>401 Unauthorized</c> otherwise.
/// </summary>
internal sealed class ManagementAuthorizationFilter : IEndpointFilter
{
    private readonly ProcessorOptions _options;

    /// <summary>
    /// Creates the filter from the bound <see cref="ProcessorOptions"/>.
    /// </summary>
    public ManagementAuthorizationFilter(IOptions<ProcessorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var authorization = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!SecretComparer.MatchesBearerToken(authorization, _options.AdminToken))
        {
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }

        return next(context);
    }
}
