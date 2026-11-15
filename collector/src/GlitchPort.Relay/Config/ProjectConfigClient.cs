using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GlitchPort.Relay.Config;

/// <summary>
/// Fetches project configuration from the Processor. Each call tries the
/// configured endpoints in order and stops at the first <c>200</c> or
/// <c>304</c> response. A dedicated <see cref="HttpClient"/>, registered by
/// <see cref="HttpClientName"/>, keeps this traffic separate from the envelope
/// publisher.
/// </summary>
public sealed class ProjectConfigClient
{
    /// <summary>Name of the dedicated project-config <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "ProjectConfig";

    /// <summary>
    /// Maximum accepted size of a Processor project-config response body, in
    /// bytes (4 MB). Larger responses are treated as a failed refresh instead of
    /// being buffered and parsed.
    /// </summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    internal const string RequestPath = "/internal/project-configs";
    internal const string InternalSecretHeader = "X-GlitchPort-Internal";
    internal const string IfNoneMatchHeader = "If-None-Match";

    private const int ReadBufferBytes = 81920;

    private readonly HttpClient _httpClient;
    private readonly ILogger<ProjectConfigClient> _logger;

    /// <summary>Creates a client over the dedicated project-config HTTP client.</summary>
    public ProjectConfigClient(HttpClient httpClient, ILogger<ProjectConfigClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the configuration from the first endpoint that answers
    /// successfully.
    /// </summary>
    /// <param name="endpoints">Processor base URLs, tried in order.</param>
    /// <param name="sharedSecret">Value of the internal authentication header.</param>
    /// <param name="knownVersion">Version to send as <c>If-None-Match</c>, if known.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    public async Task<ProjectConfigFetchResult> FetchAsync(
        IReadOnlyList<string> endpoints,
        string sharedSecret,
        string? knownVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints.Count == 0)
        {
            _logger.LogWarning(
                "No Processor endpoints are configured; cannot refresh project configuration.");
            return ProjectConfigFetchResult.Failure;
        }

        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                continue;
            }

            if (!Uri.TryCreate(
                    endpoint.Trim().TrimEnd('/') + RequestPath,
                    UriKind.Absolute,
                    out var requestUri))
            {
                _logger.LogWarning(
                    "A configured Processor project config endpoint is not a valid absolute URL.");
                continue;
            }

            // Log only the host part of the endpoint: URLs may carry credentials
            // and the rest of the path is unambiguous for this client.
            var endpointLabel = DescribeEndpoint(requestUri);

            using var request = CreateRequest(requestUri, sharedSecret, knownVersion);

            HttpResponseMessage response;
            try
            {
                // Read the headers only so the response body can be read with an
                // explicit size bound and its own timeout below.
                response = await _httpClient
                    .SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Timed out while fetching project configuration from {Endpoint}.",
                    endpointLabel);
                continue;
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to fetch project configuration from {Endpoint}.",
                    endpointLabel);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return ProjectConfigFetchResult.NotModified;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.LogWarning(
                        "Processor project config endpoint {Endpoint} returned HTTP {StatusCode}.",
                        endpointLabel,
                        (int)response.StatusCode);
                    continue;
                }

                if (response.Content is null)
                {
                    _logger.LogWarning(
                        "Processor project config endpoint {Endpoint} returned an empty response.",
                        endpointLabel);
                    continue;
                }

                if (response.Content.Headers.ContentLength is long declaredLength
                    && declaredLength > MaxResponseBytes)
                {
                    _logger.LogWarning(
                        "Processor project config endpoint {Endpoint} announced a {ContentLength} byte body, exceeding the {MaxBytes} byte limit.",
                        endpointLabel,
                        declaredLength,
                        MaxResponseBytes);
                    continue;
                }

                byte[] body;
                try
                {
                    var boundedBody = await ReadBodyAsync(response.Content, cancellationToken)
                        .ConfigureAwait(false);

                    if (boundedBody is null)
                    {
                        _logger.LogWarning(
                            "Processor project config endpoint {Endpoint} returned a body larger than the {MaxBytes} byte limit.",
                            endpointLabel,
                            MaxResponseBytes);
                        continue;
                    }

                    body = boundedBody;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Timed out while reading project configuration from {Endpoint}.",
                        endpointLabel);
                    continue;
                }
                catch (HttpRequestException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to read the project configuration from {Endpoint}.",
                        endpointLabel);
                    continue;
                }
                catch (IOException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to read the project configuration from {Endpoint}.",
                        endpointLabel);
                    continue;
                }

                ProjectConfigResponse? payload;
                try
                {
                    payload = JsonSerializer.Deserialize(
                        DecodeBody(body),
                        RelayJsonContext.Default.ProjectConfigResponse);
                }
                catch (JsonException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Processor project config endpoint {Endpoint} returned invalid JSON.",
                        endpointLabel);
                    continue;
                }

                if (payload is null)
                {
                    _logger.LogWarning(
                        "Processor project config endpoint {Endpoint} returned an empty response.",
                        endpointLabel);
                    continue;
                }

                return new ProjectConfigFetchResult(
                    ProjectConfigFetchStatus.Success,
                    ToSnapshot(payload));
            }
        }

        return ProjectConfigFetchResult.Failure;
    }

    private static HttpRequestMessage CreateRequest(
        Uri requestUri,
        string sharedSecret,
        string? knownVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation(InternalSecretHeader, sharedSecret);

        if (!string.IsNullOrEmpty(knownVersion))
        {
            request.Headers.TryAddWithoutValidation(IfNoneMatchHeader, "\"" + knownVersion + "\"");
        }

        return request;
    }

    private static string DescribeEndpoint(Uri requestUri) =>
        requestUri.IsDefaultPort ? requestUri.Host : requestUri.Authority;

    /// <summary>
    /// Reads the response body, returning <see langword="null"/> when it exceeds
    /// <see cref="MaxResponseBytes"/>. The read is bounded by the client timeout
    /// in addition to the caller's cancellation token so a stalled body cannot
    /// hold up the refresh loop.
    /// </summary>
    private async Task<byte[]?> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var timeoutSource = CreateBodyReadTimeout();
        using var linkedSource = timeoutSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
        var readToken = linkedSource?.Token ?? cancellationToken;

        await using var stream = await content
            .ReadAsStreamAsync(readToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);

        try
        {
            while (true)
            {
                var read = await stream
                    .ReadAsync(chunk.AsMemory(0, ReadBufferBytes), readToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaxResponseBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    private static readonly TimeSpan MaxTimerTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private CancellationTokenSource? CreateBodyReadTimeout()
    {
        var timeout = _httpClient.Timeout;
        if (timeout == Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
        {
            return null;
        }

        // CancellationTokenSource rejects delays above the timer range, so clamp
        // an unusually large Relay:Timeout instead of failing the refresh.
        if (timeout > MaxTimerTimeout)
        {
            timeout = MaxTimerTimeout;
        }

        return new CancellationTokenSource(timeout);
    }

    private static string DecodeBody(byte[] body)
    {
        const char byteOrderMark = '\uFEFF';

        var json = Encoding.UTF8.GetString(body);
        return json.Length > 0 && json[0] == byteOrderMark ? json[1..] : json;
    }

    private static ProjectConfigSnapshot ToSnapshot(ProjectConfigResponse payload)
    {
        var projects = new Dictionary<long, IReadOnlyDictionary<string, int?>>();

        foreach (var project in payload.Projects ?? [])
        {
            var keys = new Dictionary<string, int?>(StringComparer.Ordinal);

            foreach (var key in project.Keys ?? [])
            {
                if (!string.IsNullOrEmpty(key.PublicKey))
                {
                    keys[key.PublicKey] = key.RateLimitPerMinute;
                }
            }

            projects[project.Id] = keys;
        }

        return new ProjectConfigSnapshot(payload.Version, projects);
    }
}

/// <summary>Outcome of one attempt to fetch the project configuration.</summary>
public enum ProjectConfigFetchStatus
{
    /// <summary>A <c>200</c> response replaced the snapshot.</summary>
    Success,

    /// <summary>A <c>304</c> response kept the current snapshot.</summary>
    NotModified,

    /// <summary>Every endpoint failed.</summary>
    Failed,
}

/// <summary>Result of a project-config fetch.</summary>
/// <param name="Status">Whether the snapshot was replaced, kept or untouched.</param>
/// <param name="Snapshot">The new snapshot for <see cref="ProjectConfigFetchStatus.Success"/>.</param>
public sealed record ProjectConfigFetchResult(
    ProjectConfigFetchStatus Status,
    ProjectConfigSnapshot? Snapshot)
{
    /// <summary>A failed fetch that must leave the current snapshot untouched.</summary>
    public static ProjectConfigFetchResult Failure { get; } = new(
        ProjectConfigFetchStatus.Failed,
        null);

    /// <summary>A <c>304</c> fetch that must keep the current snapshot.</summary>
    public static ProjectConfigFetchResult NotModified { get; } = new(
        ProjectConfigFetchStatus.NotModified,
        null);
}

/// <summary>Wire format of <c>GET /internal/project-configs</c>.</summary>
internal sealed class ProjectConfigResponse
{
    /// <summary>Opaque version used for conditional requests.</summary>
    public string? Version { get; init; }

    /// <summary>Projects and their active keys.</summary>
    public List<ProjectConfigProject>? Projects { get; init; }
}

/// <summary>A project entry in the Processor project config.</summary>
internal sealed class ProjectConfigProject
{
    /// <summary>Project id.</summary>
    public long Id { get; init; }

    /// <summary>Active keys of the project.</summary>
    public List<ProjectConfigKey>? Keys { get; init; }
}

/// <summary>An active public key in the Processor project config.</summary>
internal sealed class ProjectConfigKey
{
    /// <summary>Public ingest key.</summary>
    public string PublicKey { get; init; } = string.Empty;

    /// <summary>Optional per-key requests-per-minute limit.</summary>
    public int? RateLimitPerMinute { get; init; }
}
