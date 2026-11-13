namespace GlitchPort.Transport.Http.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that records outgoing requests and
/// delegates the response to a test-provided delegate.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        _responder = responder;
    }

    /// <summary>Requests captured so far, in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var headers = request.Headers.ToDictionary(
            header => header.Key,
            header => string.Join(", ", header.Value),
            StringComparer.OrdinalIgnoreCase);

        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            headers,
            request.Content?.Headers.ContentType?.MediaType,
            body);

        lock (_gate)
        {
            _requests.Add(recorded);
        }

        return await _responder(request, cancellationToken);
    }
}

/// <summary>A request snapshot that stays valid after the request is disposed.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    byte[] Body);
