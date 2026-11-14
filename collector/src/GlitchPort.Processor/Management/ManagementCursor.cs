using System.Buffers.Text;
using System.Text.Json;

namespace GlitchPort.Processor.Management;

/// <summary>
/// Encodes and decodes the opaque, URL-safe cursors used for keyset
/// pagination.
/// </summary>
/// <remarks>
/// A cursor is the base64url encoding of a two-element JSON array holding the
/// sort value and the id of the last row of a page. Clients must treat it as
/// opaque: it is only valid together with the endpoint, the sort and the
/// filters that produced it. Any cursor that cannot be decoded is rejected
/// with <c>400 Bad Request</c> by the endpoints.
/// </remarks>
internal static class ManagementCursor
{
    // A cursor only ever contains a timestamp or a count plus an id, so
    // anything larger than this cannot have been produced by this service.
    private const int MaxEncodedLength = 512;

    /// <summary>
    /// Encodes <paramref name="sortValue"/> and <paramref name="id"/> into an
    /// opaque cursor.
    /// </summary>
    public static string Encode(string sortValue, string id)
    {
        ArgumentNullException.ThrowIfNull(sortValue);
        ArgumentNullException.ThrowIfNull(id);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(sortValue);
            writer.WriteStringValue(id);
            writer.WriteEndArray();
        }

        return Base64Url.EncodeToString(buffer.ToArray());
    }

    /// <summary>
    /// Decodes an opaque cursor into its sort value and id.
    /// </summary>
    /// <returns><see langword="false"/> when the cursor is malformed.</returns>
    public static bool TryDecode(string cursor, out string sortValue, out string id)
    {
        sortValue = string.Empty;
        id = string.Empty;

        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxEncodedLength)
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2)
            {
                return false;
            }

            var sortElement = root[0];
            var idElement = root[1];
            if (sortElement.ValueKind != JsonValueKind.String || idElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            sortValue = sortElement.GetString() ?? string.Empty;
            id = idElement.GetString() ?? string.Empty;
            return sortValue.Length > 0 && id.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
