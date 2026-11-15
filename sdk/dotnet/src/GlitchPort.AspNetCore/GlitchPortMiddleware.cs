using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace GlitchPort.AspNetCore;

/// <summary>
/// Pushes a per-request scope, records the request context and captures
/// unhandled request exceptions.
/// </summary>
/// <remarks>
/// The middleware deliberately rethrows the original exception so an exception
/// page or exception handler registered before it in the pipeline still works.
/// </remarks>
internal sealed class GlitchPortMiddleware
{
    private static readonly string[] CapturedHeaderNames = { "User-Agent", "Content-Type", "Accept" };

    private readonly RequestDelegate _next;
    private readonly GlitchPortClient _client;

    public GlitchPortMiddleware(RequestDelegate next, GlitchPortClient client)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var scope = _client.PushScope();

        ConfigureRequestScope(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // An exception instance is reported at most once, no matter whether
            // the application logged it first or the middleware sees it first.
            if (GlitchPortExceptionMarker.TryMark(exception))
            {
                try
                {
                    await _client.CaptureExceptionAsync(exception, Level.Error).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Capture must never replace the exception the application
                    // (or the exception page) is about to observe.
                }
            }

            throw;
        }
    }

    private void ConfigureRequestScope(HttpContext context)
    {
        var request = context.Request;
        var route = GetRoutePattern(context);
        var remoteIpAddress = context.Connection.RemoteIpAddress?.ToString();
        var sendDefaultPii = _client.Options.SendDefaultPii;
        var userName = sendDefaultPii ? GetAuthenticatedUserName(context) : null;

        var requestContext = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["method"] = request.Method,
            ["url"] = GetPath(request),
        };

        if (route is not null)
        {
            requestContext["route"] = route;
        }

        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < CapturedHeaderNames.Length; i++)
        {
            var name = CapturedHeaderNames[i];
            if (request.Headers.TryGetValue(name, out var values) && !StringValues.IsNullOrEmpty(values))
            {
                headers[name] = values.ToString();
            }
        }

        requestContext["headers"] = headers;

        if (sendDefaultPii)
        {
            if (!string.IsNullOrEmpty(remoteIpAddress))
            {
                requestContext["client_ip"] = remoteIpAddress;
            }
        }

        _client.ConfigureScope(scope =>
        {
            scope.SetContext("request", requestContext);
            scope.SetTag("http.method", request.Method);

            if (route is not null)
            {
                scope.SetTag("http.route", route);
            }

            if (sendDefaultPii && (!string.IsNullOrEmpty(userName) || !string.IsNullOrEmpty(remoteIpAddress)))
            {
                scope.SetUser(new UserInfo
                {
                    Username = userName,
                    IpAddress = remoteIpAddress,
                });
            }
        });
    }

    private static string? GetAuthenticatedUserName(HttpContext context)
    {
        var identity = context.User?.Identity;
        return identity is { IsAuthenticated: true } && !string.IsNullOrEmpty(identity.Name)
            ? identity.Name
            : null;
    }

    private static string? GetRoutePattern(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint endpoint)
        {
            var rawText = endpoint.RoutePattern.RawText;
            return string.IsNullOrEmpty(rawText) ? null : rawText;
        }

        return null;
    }

    private static string GetPath(HttpRequest request)
    {
        var pathBase = request.PathBase.Value;
        var path = request.Path.Value;

        if (string.IsNullOrEmpty(pathBase))
        {
            return string.IsNullOrEmpty(path) ? "/" : path;
        }

        return string.IsNullOrEmpty(path) ? pathBase : pathBase + path;
    }
}
