using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlitchPort.Processor.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Project and public key management endpoints.
/// </summary>
/// <remarks>
/// A project always gets a DSN derived from <c>Processor:PublicRelayUrl</c>, so
/// clients can copy it from the create, detail and key responses. Deleting a
/// project removes its issues, events, labels and keys through the cascading
/// foreign keys; keys are only ever revoked, never deleted.
/// </remarks>
internal static class ManagementProjectEndpoints
{
    private const string DefaultSlug = "project";

    private static readonly Regex SlugPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Maps the project and key endpoints on <paramref name="group"/>.</summary>
    public static RouteGroupBuilder MapProjectEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/projects", ListProjectsAsync);
        group.MapPost("/projects", CreateProjectAsync);
        group.MapGet("/projects/{projectId:long}", GetProjectAsync);
        group.MapPatch("/projects/{projectId:long}", UpdateProjectAsync);
        group.MapDelete("/projects/{projectId:long}", DeleteProjectAsync);
        group.MapGet("/projects/{projectId:long}/keys", ListKeysAsync);
        group.MapPost("/projects/{projectId:long}/keys", CreateKeyAsync);
        group.MapPatch("/keys/{keyId:long}", UpdateKeyAsync);
        group.MapDelete("/keys/{keyId:long}", DeleteKeyAsync);

        return group;
    }

    private static async Task<IResult> ListProjectsAsync(
        ProjectStore store,
        CancellationToken cancellationToken)
    {
        var projects = await store.ListAsync(cancellationToken).ConfigureAwait(false);

        var response = projects
            .Select(project => new ProjectListItemResponse(
                project.Id,
                project.Name,
                project.Slug,
                project.Platform,
                project.CreatedAt,
                project.UnresolvedIssueCount,
                project.ActiveKeyCount))
            .ToList();

        return ManagementHttp.Json(response, ProcessorJsonContext.Default.ListProjectListItemResponse);
    }

    private static async Task<IResult> CreateProjectAsync(
        HttpRequest request,
        ProjectStore store,
        IOptions<ProcessorOptions> options,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.CreateProjectRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return ManagementHttp.BadRequest("A JSON body with a name is required.");
        }

        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return ManagementHttp.BadRequest("name is required.");
        }

        var name = body.Name.Trim();

        string slug;
        if (body.Slug is null)
        {
            slug = DeriveSlug(name);
        }
        else
        {
            slug = body.Slug.Trim();
            if (slug.Length == 0)
            {
                return ManagementHttp.BadRequest("slug must not be blank.");
            }

            if (!SlugPattern.IsMatch(slug))
            {
                return ManagementHttp.BadRequest(
                    "slug must contain only lowercase letters, digits and single hyphens.");
            }
        }

        string? platform = null;
        if (body.Platform is not null)
        {
            platform = body.Platform.Trim();
            if (platform.Length == 0)
            {
                return ManagementHttp.BadRequest("platform must not be blank.");
            }
        }

        var created = await store
            .CreateAsync(name, slug, platform, cancellationToken)
            .ConfigureAwait(false);

        if (created is null)
        {
            return ManagementHttp.Conflict($"A project with slug '{slug}' already exists.");
        }

        var dsn = BuildDsn(options.Value.PublicRelayUrl, created.Key, created.Project.Id);
        var response = new CreatedProjectResponse(
            created.Project.Id,
            created.Project.Name,
            created.Project.Slug,
            created.Project.Platform,
            created.Project.CreatedAt,
            dsn);

        return ManagementHttp.Json(
            response,
            ProcessorJsonContext.Default.CreatedProjectResponse,
            StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetProjectAsync(
        long projectId,
        ProjectStore store,
        IOptions<ProcessorOptions> options,
        CancellationToken cancellationToken)
    {
        var project = await ReadDetailAsync(store, projectId, options.Value.PublicRelayUrl, cancellationToken)
            .ConfigureAwait(false);

        return project is null
            ? ManagementHttp.NotFound($"Project {projectId} was not found.")
            : ManagementHttp.Json(project, ProcessorJsonContext.Default.ProjectDetailResponse);
    }

    private static async Task<IResult> UpdateProjectAsync(
        long projectId,
        HttpRequest request,
        ProjectStore store,
        IOptions<ProcessorOptions> options,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.UpdateProjectRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return ManagementHttp.BadRequest("A JSON body with a name and/or platform is required.");
        }

        string? name = null;
        if (body.Name is not null)
        {
            name = body.Name.Trim();
            if (name.Length == 0)
            {
                return ManagementHttp.BadRequest("name must not be blank.");
            }
        }

        string? platform = null;
        if (body.Platform is not null)
        {
            platform = body.Platform.Trim();
            if (platform.Length == 0)
            {
                return ManagementHttp.BadRequest("platform must not be blank.");
            }
        }

        var updated = await store
            .UpdateAsync(projectId, name, platform, cancellationToken)
            .ConfigureAwait(false);

        if (updated is null)
        {
            return ManagementHttp.NotFound($"Project {projectId} was not found.");
        }

        var project = await ReadDetailAsync(store, projectId, options.Value.PublicRelayUrl, cancellationToken)
            .ConfigureAwait(false);

        return project is null
            ? ManagementHttp.NotFound($"Project {projectId} was not found.")
            : ManagementHttp.Json(project, ProcessorJsonContext.Default.ProjectDetailResponse);
    }

    private static async Task<IResult> DeleteProjectAsync(
        long projectId,
        ProjectStore store,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteAsync(projectId, cancellationToken).ConfigureAwait(false);

        return deleted
            ? Results.NoContent()
            : ManagementHttp.NotFound($"Project {projectId} was not found.");
    }

    private static async Task<IResult> ListKeysAsync(
        long projectId,
        ProjectStore store,
        IOptions<ProcessorOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await store.ExistsAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return ManagementHttp.NotFound($"Project {projectId} was not found.");
        }

        var keys = await store.ListKeysAsync(projectId, cancellationToken).ConfigureAwait(false);
        var response = keys
            .Select(key => ToResponse(key, options.Value.PublicRelayUrl))
            .ToList();

        return ManagementHttp.Json(response, ProcessorJsonContext.Default.ListProjectKeyResponse);
    }

    private static async Task<IResult> CreateKeyAsync(
        long projectId,
        HttpRequest request,
        ProjectStore store,
        IOptions<ProcessorOptions> options,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.CreateProjectKeyRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return ManagementHttp.BadRequest("A JSON body with an optional label and rate limit is required.");
        }

        if (body.RateLimitPerMinute is < 0)
        {
            return ManagementHttp.BadRequest("rate_limit_per_minute must not be negative.");
        }

        var key = await store
            .CreateKeyAsync(
                projectId,
                NormalizeLabel(body.Label),
                body.RateLimitPerMinute,
                cancellationToken)
            .ConfigureAwait(false);

        return key is null
            ? ManagementHttp.NotFound($"Project {projectId} was not found.")
            : ManagementHttp.Json(
                ToResponse(key, options.Value.PublicRelayUrl),
                ProcessorJsonContext.Default.ProjectKeyResponse,
                StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateKeyAsync(
        long keyId,
        HttpRequest request,
        ProjectStore store,
        IOptions<ProcessorOptions> options,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.UpdateProjectKeyRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return ManagementHttp.BadRequest("A JSON body with label, rate_limit_per_minute and/or is_active is required.");
        }

        var label = OptionalField<string?>.Unspecified;
        if (body.Label.ValueKind != JsonValueKind.Undefined)
        {
            if (body.Label.ValueKind == JsonValueKind.Null)
            {
                label = OptionalField<string?>.Specified(null);
            }
            else if (body.Label.ValueKind == JsonValueKind.String)
            {
                label = OptionalField<string?>.Specified(NormalizeLabel(body.Label.GetString()));
            }
            else
            {
                return ManagementHttp.BadRequest("label must be a string or null.");
            }
        }

        var rateLimit = OptionalField<int?>.Unspecified;
        if (body.RateLimitPerMinute.ValueKind != JsonValueKind.Undefined)
        {
            switch (body.RateLimitPerMinute.ValueKind)
            {
                case JsonValueKind.Null:
                    rateLimit = OptionalField<int?>.Specified(null);
                    break;

                case JsonValueKind.Number
                    when body.RateLimitPerMinute.TryGetInt32(out var value) && value >= 0:
                    rateLimit = OptionalField<int?>.Specified(value);
                    break;

                default:
                    return ManagementHttp.BadRequest(
                        "rate_limit_per_minute must be a non-negative integer or null.");
            }
        }

        var isActive = OptionalField<bool>.Unspecified;
        if (body.IsActive.ValueKind != JsonValueKind.Undefined)
        {
            switch (body.IsActive.ValueKind)
            {
                case JsonValueKind.True:
                    isActive = OptionalField<bool>.Specified(true);
                    break;

                case JsonValueKind.False:
                    isActive = OptionalField<bool>.Specified(false);
                    break;

                default:
                    return ManagementHttp.BadRequest("is_active must be a boolean.");
            }
        }

        var key = await store
            .UpdateKeyAsync(
                keyId,
                new ProjectKeyUpdate(label, rateLimit, isActive),
                cancellationToken)
            .ConfigureAwait(false);

        return key is null
            ? ManagementHttp.NotFound($"Key {keyId} was not found.")
            : ManagementHttp.Json(
                ToResponse(key, options.Value.PublicRelayUrl),
                ProcessorJsonContext.Default.ProjectKeyResponse);
    }

    private static async Task<IResult> DeleteKeyAsync(
        long keyId,
        ProjectStore store,
        CancellationToken cancellationToken)
    {
        var revoked = await store.RevokeKeyAsync(keyId, cancellationToken).ConfigureAwait(false);

        return revoked is null
            ? ManagementHttp.NotFound($"Key {keyId} was not found.")
            : Results.NoContent();
    }

    private static async Task<ProjectDetailResponse?> ReadDetailAsync(
        ProjectStore store,
        long projectId,
        string publicRelayUrl,
        CancellationToken cancellationToken)
    {
        var project = await store.GetSummaryAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var keys = await store.ListKeysAsync(projectId, cancellationToken).ConfigureAwait(false);
        var activeKey = keys.FirstOrDefault(static key => key.IsActive);

        return new ProjectDetailResponse(
            project.Id,
            project.Name,
            project.Slug,
            project.Platform,
            project.CreatedAt,
            project.UnresolvedIssueCount,
            project.ActiveKeyCount,
            activeKey is null ? null : BuildDsn(publicRelayUrl, activeKey, project.Id));
    }

    private static ProjectKeyResponse ToResponse(ProjectKeyRecord key, string publicRelayUrl) => new(
        key.Id,
        key.PublicKey,
        key.Label,
        key.IsActive,
        key.RateLimitPerMinute,
        key.CreatedAt,
        key.RevokedAt,
        BuildDsn(publicRelayUrl, key, key.ProjectId));

    private static string BuildDsn(string publicRelayUrl, ProjectKeyRecord key, long projectId) =>
        DsnBuilder.Build(publicRelayUrl, key.PublicKey, projectId);

    private static string? NormalizeLabel(string? label)
    {
        if (label is null)
        {
            return null;
        }

        var trimmed = label.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string DeriveSlug(string name)
    {
        var builder = new StringBuilder(name.Length);
        var separatorPending = false;

        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (separatorPending && builder.Length > 0)
                {
                    builder.Append('-');
                }

                separatorPending = false;
                builder.Append(character);
            }
            else
            {
                separatorPending = true;
            }
        }

        return builder.Length == 0 ? DefaultSlug : builder.ToString();
    }
}
