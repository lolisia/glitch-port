using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlitchPort.Processor.Projects;
using GlitchPort.Processor.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace GlitchPort.Processor.Endpoints;

/// <summary>
/// Internal endpoint that lets Relay instances fetch the projects and active
/// keys they need to authenticate and rate limit incoming events.
/// </summary>
internal static class ProjectConfigEndpoints
{
    private const string InternalSecretHeader = "X-GlitchPort-Internal";

    /// <summary>Maps the internal project configuration endpoint.</summary>
    public static IEndpointRouteBuilder MapProjectConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/internal/project-configs", GetProjectConfigsAsync);

        return endpoints;
    }

    private static async Task<IResult> GetProjectConfigsAsync(
        HttpRequest request,
        HttpResponse response,
        ProjectStore store,
        IOptions<ProcessorOptions> options,
        CancellationToken cancellationToken)
    {
        if (!SecretComparer.FixedTimeEquals(
                request.Headers[InternalSecretHeader].ToString(),
                options.Value.InternalSharedSecret))
        {
            return Results.Unauthorized();
        }

        var configs = await store.ListConfigsAsync(cancellationToken).ConfigureAwait(false);

        var projects = configs
            .Select(static project => new ProjectConfigProject(
                project.Id,
                project.Keys
                    .Select(static key => new ProjectConfigKey(key.PublicKey, key.RateLimitPerMinute))
                    .ToList(),
                new ProjectConfigInboundFilters(
                    project.InboundFilters.Releases,
                    project.InboundFilters.Environments,
                    project.InboundFilters.MessagePatterns)))
            .ToList();

        var version = ComputeVersion(projects);
        var etag = $"\"{version}\"";

        response.Headers.ETag = etag;

        if (MatchesIfNoneMatch(request.Headers.IfNoneMatch, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var body = new ProjectConfigResponse(version, projects);

        return Results.Json(body, ProcessorJsonContext.Default.ProjectConfigResponse);
    }

    /// <summary>
    /// Hashes the canonical JSON of the projects payload. The payload contains
    /// only stable fields (ids, active keys, limits and inbound filters), so the
    /// version stays the same while the data is unchanged and changes when a
    /// project or key is added, removed, rate limited or revoked, or when an
    /// inbound filter is edited.
    /// </summary>
    private static string ComputeVersion(List<ProjectConfigProject> projects)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            projects,
            ProcessorJsonContext.Default.ListProjectConfigProject);

        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    private static bool MatchesIfNoneMatch(StringValues ifNoneMatch, string etag)
    {
        foreach (var header in ifNoneMatch)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            foreach (var rawCandidate in header.Split(','))
            {
                var candidate = rawCandidate.Trim();
                if (candidate == "*")
                {
                    return true;
                }

                if (candidate.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = candidate[2..].Trim();
                }

                if (string.Equals(candidate, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>Response body of <c>GET /internal/project-configs</c>.</summary>
internal sealed record ProjectConfigResponse(
    string Version,
    IReadOnlyList<ProjectConfigProject> Projects);

/// <summary>A project in the Relay configuration, with only its active keys.</summary>
internal sealed record ProjectConfigProject(
    long Id,
    IReadOnlyList<ProjectConfigKey> Keys,
    ProjectConfigInboundFilters InboundFilters);

/// <summary>An active public key and its optional per-minute limit.</summary>
internal sealed record ProjectConfigKey(string PublicKey, int? RateLimitPerMinute);

/// <summary>The inbound filters a Relay must apply to a project's events.</summary>
internal sealed record ProjectConfigInboundFilters(
    IReadOnlyList<string> Releases,
    IReadOnlyList<string> Environments,
    IReadOnlyList<string> MessagePatterns);
