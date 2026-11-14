using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Label list, create, update and delete endpoints.
/// </summary>
internal static class ManagementLabelEndpoints
{
    private const string DefaultLabelColor = "#888888";

    /// <summary>Maps the label endpoints on <paramref name="group"/>.</summary>
    public static RouteGroupBuilder MapLabelEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/projects/{projectId:long}/labels", ListLabelsAsync);
        group.MapPost("/projects/{projectId:long}/labels", CreateLabelAsync);
        group.MapPatch("/labels/{labelId:long}", UpdateLabelAsync);
        group.MapDelete("/labels/{labelId:long}", DeleteLabelAsync);

        return group;
    }

    private static async Task<IResult> ListLabelsAsync(
        long projectId,
        ManagementLabelStore labels,
        CancellationToken cancellationToken)
    {
        var items = await labels.ListAsync(projectId, cancellationToken).ConfigureAwait(false);

        return ManagementHttp.Json(items, ProcessorJsonContext.Default.ListLabelResponse);
    }

    private static async Task<IResult> CreateLabelAsync(
        long projectId,
        HttpRequest request,
        ManagementLabelStore labels,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.CreateLabelRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return ManagementHttp.BadRequest("A JSON body with a name is required.");
        }

        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return ManagementHttp.BadRequest("name is required.");
        }

        var color = body.Color is null ? DefaultLabelColor : body.Color.Trim();
        if (color.Length == 0)
        {
            return ManagementHttp.BadRequest("color must not be blank.");
        }

        var result = await labels
            .CreateAsync(projectId, body.Name.Trim(), color, cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            LabelMutationStatus.Ok => ManagementHttp.Json(
                result.Label!,
                ProcessorJsonContext.Default.LabelResponse,
                StatusCodes.Status201Created),
            _ => ManagementHttp.Conflict($"A label named '{body.Name.Trim()}' already exists in this project."),
        };
    }

    private static async Task<IResult> UpdateLabelAsync(
        long labelId,
        HttpRequest request,
        ManagementLabelStore labels,
        CancellationToken cancellationToken)
    {
        var body = await ManagementHttp
            .ReadJsonAsync(request, ProcessorJsonContext.Default.UpdateLabelRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            return ManagementHttp.BadRequest("A JSON body with a name and/or color is required.");
        }

        string? name = null;
        if (body.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return ManagementHttp.BadRequest("name must not be blank.");
            }

            name = body.Name.Trim();
        }

        string? color = null;
        if (body.Color is not null)
        {
            color = body.Color.Trim();
            if (color.Length == 0)
            {
                return ManagementHttp.BadRequest("color must not be blank.");
            }
        }

        var result = await labels.UpdateAsync(labelId, name, color, cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            LabelMutationStatus.Ok => ManagementHttp.Json(
                result.Label!,
                ProcessorJsonContext.Default.LabelResponse),
            LabelMutationStatus.Conflict => ManagementHttp.Conflict(
                "A label with that name already exists in this project."),
            _ => ManagementHttp.NotFound($"Label {labelId} was not found."),
        };
    }

    private static async Task<IResult> DeleteLabelAsync(
        long labelId,
        ManagementLabelStore labels,
        CancellationToken cancellationToken)
    {
        var deleted = await labels.DeleteAsync(labelId, cancellationToken).ConfigureAwait(false);

        return deleted
            ? Results.NoContent()
            : ManagementHttp.NotFound($"Label {labelId} was not found.");
    }
}
