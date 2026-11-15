using System.Text.Json.Nodes;

namespace GlitchPort.AspNetCore.Tests.Infrastructure;

/// <summary>Path-based helpers for asserting on the captured protocol v1 JSON.</summary>
internal static class JsonAccess
{
    public static bool Has(JsonObject root, params string[] path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        JsonNode? current = root;
        for (var i = 0; i < path.Length; i++)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(path[i], out var next))
            {
                return false;
            }

            current = next;
        }

        return true;
    }

    public static string? GetString(JsonObject root, params string[] path)
    {
        return GetNode(root, path) is JsonValue value
            && value.TryGetValue<string>(out var text)
                ? text
                : null;
    }

    public static JsonObject? GetObject(JsonObject root, params string[] path)
    {
        return GetNode(root, path) as JsonObject;
    }

    public static JsonArray? GetArray(JsonObject root, params string[] path)
    {
        return GetNode(root, path) as JsonArray;
    }

    public static string[] GetStringArray(JsonObject root, params string[] path)
    {
        return GetArray(root, path) is { } array
            ? array
                .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray()
            : Array.Empty<string>();
    }

    private static JsonNode? GetNode(JsonObject root, string[] path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        JsonNode? current = root;
        for (var i = 0; i < path.Length; i++)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(path[i], out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}
