using Microsoft.AspNetCore.Builder;

namespace GlitchPort.AspNetCore;

/// <summary>
/// Pipeline registration for the glitch-port request middleware.
/// </summary>
public static class GlitchPortApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the glitch-port request middleware. It pushes a scope for every
    /// request, fills in the <c>request</c> context and the <c>http.method</c>
    /// and <c>http.route</c> tags, and captures unhandled request exceptions at
    /// <see cref="Level.Error"/>.
    /// </summary>
    /// <param name="app">The application builder to update.</param>
    /// <returns><paramref name="app"/> so the pipeline can be chained.</returns>
    /// <remarks>
    /// <para>
    /// Call this method <b>after</b> <c>UseRouting()</c>: the middleware reads the
    /// route pattern from <c>HttpContext.GetEndpoint()</c>, and routing selects
    /// the endpoint. With minimal APIs the implicit routing middleware already
    /// runs before user middleware, so a plain <c>app.UseGlitchPort()</c> after
    /// the endpoint-independent middleware is enough. When authentication is
    /// used, place <c>UseGlitchPort()</c> after <c>UseAuthentication()</c> so the
    /// authenticated identity is available for <c>user.username</c> when
    /// <see cref="GlitchPortOptions.SendDefaultPii"/> is enabled.
    /// </para>
    /// <para>
    /// The middleware always rethrows the original exception after capturing it.
    /// An exception page or exception handler placed <b>before</b> it (for
    /// example <c>UseDeveloperExceptionPage()</c> or <c>UseExceptionHandler()</c>)
    /// therefore still handles the request, and outer middleware observes the
    /// same exception instance and stack trace.
    /// </para>
    /// <para>
    /// When the application is hosted behind a reverse proxy, run the forwarded
    /// headers middleware before this one so <c>client_ip</c> and
    /// <c>user.ip_address</c> describe the real client. Both values are only
    /// recorded when <see cref="GlitchPortOptions.SendDefaultPii"/> is enabled.
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseGlitchPort(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<GlitchPortMiddleware>();
    }
}
