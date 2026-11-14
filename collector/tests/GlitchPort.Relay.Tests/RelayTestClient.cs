using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GlitchPort.Collector.Contracts;
using Xunit;

namespace GlitchPort.Relay.Tests;

/// <summary>Shared HTTP helpers for the Relay endpoint tests.</summary>
internal static class RelayTestClient
{
    /// <summary>Public key configured for project 1 by <see cref="RelayApplicationFactory"/>.</summary>
    public const string ValidKey = "test-key";

    /// <summary>Project id configured by <see cref="RelayApplicationFactory"/>.</summary>
    public const long ProjectId = 1;

    /// <summary>Header the test host uses to choose the request remote address.</summary>
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    /// <summary>Remote address the test host assigns when the header is absent.</summary>
    public const string DefaultRemoteIp = "203.0.113.9";

    private static readonly string RepositoryRootPath = FindRepositoryRoot();

    /// <summary>Absolute path of the repository root.</summary>
    public static string RepositoryRoot => RepositoryRootPath;

    /// <summary>Path of the protocol v1 event endpoint.</summary>
    public static string EventsPath(long projectId = ProjectId) => $"/api/{projectId}/events";

    /// <summary>Path of the legacy messages endpoint.</summary>
    public static string MessagesPath(long projectId = ProjectId) => $"/api/{projectId}/messages";

    /// <summary>Posts a JSON body, optionally gzip-compressed.</summary>
    public static Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        string json,
        string? key = ValidKey,
        bool gzip = false,
        string? contentEncoding = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var bytes = gzip ? CompressJson(json) : Encoding.UTF8.GetBytes(json);
        var encoding = contentEncoding ?? (gzip ? "gzip" : null);

        return PostBytesAsync(client, path, bytes, key, encoding, headers);
    }

    /// <summary>Posts a raw body with an optional content encoding.</summary>
    public static async Task<HttpResponseMessage> PostBytesAsync(
        HttpClient client,
        string path,
        byte[] body,
        string? key = ValidKey,
        string? contentEncoding = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(body);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (contentEncoding is not null)
        {
            request.Content.Headers.ContentEncoding.Add(contentEncoding);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("X-GlitchPort-Key", key);
        }

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        try
        {
            return await client.SendAsync(request).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>Posts a protocol v1 event body.</summary>
    public static Task<HttpResponseMessage> PostEventAsync(
        HttpClient client,
        string json,
        string? key = ValidKey,
        bool gzip = false,
        string? contentEncoding = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        PostJsonAsync(
            client,
            EventsPath(),
            json,
            key,
            gzip,
            contentEncoding,
            headers);

    /// <summary>Posts a legacy message body.</summary>
    public static Task<HttpResponseMessage> PostMessageAsync(
        HttpClient client,
        string json,
        string? key = ValidKey,
        bool gzip = false,
        string? contentEncoding = null) =>
        PostJsonAsync(
            client,
            MessagesPath(),
            json,
            key,
            gzip,
            contentEncoding);

    /// <summary>Reads a response body as a detached JSON element.</summary>
    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>Reads the <c>error</c> field of a failed response.</summary>
    public static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        var json = await ReadJsonAsync(response).ConfigureAwait(false);
        return json.GetProperty("error").GetString() ?? string.Empty;
    }

    /// <summary>Returns the normalized payload of an envelope.</summary>
    public static JsonElement Payload(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Assert.NotNull(envelope.Payload);
        return envelope.Payload!.Value;
    }

    /// <summary>Reads a string property of a normalized payload.</summary>
    public static string? PayloadString(Envelope envelope, string property)
    {
        var payload = Payload(envelope);
        return payload.TryGetProperty(property, out var value)
            ? value.GetString()
            : null;
    }

    /// <summary>Gzip-compresses a JSON body.</summary>
    public static byte[] CompressJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var bytes = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(
                Path.Combine(
                    directory.FullName,
                    "sdk",
                    "protocol",
                    "event.v1.schema.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root from '{AppContext.BaseDirectory}'.");
    }
}
