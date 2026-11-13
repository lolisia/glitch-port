using System.Net.Http.Headers;
using System.Text.Json;
using GlitchPort.Collector.Contracts;

namespace GlitchPort.Transport.Http;

/// <summary>
/// Publishes envelope batches to Processor instances over HTTP.
/// </summary>
/// <remarks>
/// Every publish starts at the next endpoint in a thread-safe rotation and
/// walks the endpoint list from there. A failed attempt moves on to the next
/// endpoint when the failure is retryable (connection error, per-attempt
/// timeout or HTTP 5xx). Non-retryable responses fail immediately.
/// </remarks>
public sealed class HttpEnvelopePublisher : IEnvelopePublisher
{
    private const string InternalHeaderName = "X-GlitchPort-Internal";
    private const string InternalEnvelopesPath = "/internal/envelopes";
    private const string JsonMediaType = "application/json";

    private readonly HttpTransportOptions _options;
    private readonly HttpClient _httpClient;

    // -1 so the first Interlocked.Increment yields 0 (the first endpoint).
    private int _publishCounter = -1;

    /// <summary>
    /// Creates a publisher that sends envelopes through <paramref name="httpClient"/>.
    /// </summary>
    public HttpEnvelopePublisher(HttpTransportOptions options, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);

        if (options.Timeout < TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Timeout,
                "Timeout must be zero or greater, or Timeout.InfiniteTimeSpan.");
        }

        _options = options;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(IReadOnlyList<Envelope> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ct.ThrowIfCancellationRequested();

        var endpoints = _options.Endpoints;
        if (endpoints is null || endpoints.Length == 0)
        {
            throw new EnvelopePublishException("No envelope endpoints are configured.", []);
        }

        // Serialize once; every attempt sends the identical payload.
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            batch.ToArray(),
            ContractsJsonContext.Default.EnvelopeArray);

        var startIndex = NextStartIndex(endpoints.Length);
        var attemptedEndpoints = new List<string>(endpoints.Length);
        Exception? lastError = null;

        for (var offset = 0; offset < endpoints.Length; offset++)
        {
            ct.ThrowIfCancellationRequested();

            var endpoint = endpoints[(startIndex + offset) % endpoints.Length];
            attemptedEndpoints.Add(endpoint);

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(_options.Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpointUrl(endpoint));
            request.Headers.TryAddWithoutValidation(InternalHeaderName, _options.SharedSecret);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType);

            try
            {
                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var statusError = new HttpRequestException(
                    $"Endpoint '{endpoint}' returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    inner: null,
                    response.StatusCode);

                if ((int)response.StatusCode < 500)
                {
                    throw new EnvelopePublishException(statusError.Message, attemptedEndpoints, statusError);
                }

                lastError = statusError;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The linked token fired because this attempt exceeded the timeout.
                lastError = new TimeoutException(
                    $"Endpoint '{endpoint}' did not complete within {_options.Timeout.TotalMilliseconds:0} ms.");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
        }

        ct.ThrowIfCancellationRequested();

        throw new EnvelopePublishException(
            $"All {attemptedEndpoints.Count} envelope endpoint(s) failed. Attempted: {string.Join(", ", attemptedEndpoints)}.",
            attemptedEndpoints,
            lastError);
    }

    private int NextStartIndex(int endpointCount)
    {
        var sequence = Interlocked.Increment(ref _publishCounter);

        // Cast so the modulo stays correct even after the counter wraps.
        return (int)((uint)sequence % (uint)endpointCount);
    }

    private static string BuildEndpointUrl(string endpoint) =>
        string.Concat(endpoint.TrimEnd('/'), InternalEnvelopesPath);
}
