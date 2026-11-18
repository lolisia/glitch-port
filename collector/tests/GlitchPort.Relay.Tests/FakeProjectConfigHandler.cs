using System.Net;
using System.Text;
using System.Text.Json;
using GlitchPort.Relay.Config;
using GlitchPort.Relay.Filtering;

namespace GlitchPort.Relay.Tests;

/// <summary>
/// Deterministic <see cref="HttpMessageHandler"/> for the Processor project
/// config endpoint. Responses can be queued per request and every request is
/// recorded for header/order assertions.
/// </summary>
internal sealed class FakeProjectConfigHandler : HttpMessageHandler
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<RecordedProjectConfigRequest> _requests = [];
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = [];
    private readonly List<RequestWaiter> _waiters = [];
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _defaultResponder;

    public FakeProjectConfigHandler()
        : this(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
    {
    }

    public FakeProjectConfigHandler(Func<HttpRequestMessage, HttpResponseMessage> defaultResponder)
    {
        ArgumentNullException.ThrowIfNull(defaultResponder);
        _defaultResponder = defaultResponder;
    }

    /// <summary>Requests received so far, in order.</summary>
    public IReadOnlyList<RecordedProjectConfigRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Queues a response factory for the next request.</summary>
    public FakeProjectConfigHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);

        lock (_gate)
        {
            _responses.Enqueue(responder);
        }

        return this;
    }

    /// <summary>Queues a response with the supplied status code.</summary>
    public FakeProjectConfigHandler EnqueueStatus(HttpStatusCode statusCode) =>
        Enqueue(_ => new HttpResponseMessage(statusCode));

    /// <summary>Queues a <c>200</c> project config response without inbound filters.</summary>
    public FakeProjectConfigHandler EnqueueConfig(
        string version,
        params (long ProjectId, string PublicKey, int? RateLimitPerMinute)[] keys) =>
        Enqueue(_ => JsonResponse(ConfigJson(version, keys)));

    /// <summary>Queues a <c>200</c> project config response with inbound filters.</summary>
    public FakeProjectConfigHandler EnqueueConfig(
        string version,
        IReadOnlyDictionary<long, InboundFilterConfig> inboundFilters,
        params (long ProjectId, string PublicKey, int? RateLimitPerMinute)[] keys) =>
        Enqueue(_ => JsonResponse(ConfigJson(version, inboundFilters, keys)));

    /// <summary>Queues a <c>200</c> response with an arbitrary body.</summary>
    public FakeProjectConfigHandler EnqueueJson(string json) =>
        Enqueue(_ => JsonResponse(json));

    /// <summary>
    /// Queues a <c>200</c> response whose declared body length exceeds the
    /// project-config response limit without allocating the body.
    /// </summary>
    public FakeProjectConfigHandler EnqueueOversizedResponse() =>
        Enqueue(_ =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = ProjectConfigClient.MaxResponseBytes + 1;

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    /// <summary>Waits until at least <paramref name="count"/> requests were received.</summary>
    /// <param name="count">Number of requests to wait for.</param>
    /// <param name="timeout">
    /// Upper bound for the wait; defaults to ten seconds so a regression fails
    /// the test instead of hanging it.
    /// </param>
    /// <exception cref="TimeoutException">Fewer requests arrived in time.</exception>
    public Task WaitForRequestCountAsync(int count, TimeSpan? timeout = null)
    {
        if (count <= 0)
        {
            return Task.CompletedTask;
        }

        Task completion;
        lock (_gate)
        {
            if (_requests.Count >= count)
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new RequestWaiter(count, waiter));
            completion = waiter.Task;
        }

        return WaitWithTimeoutAsync(completion, count, timeout ?? DefaultWaitTimeout);
    }

    /// <summary>
    /// Builds a Processor project config payload with snake_case fields. The
    /// legacy overload omits <c>inbound_filters</c> so it keeps representing a
    /// Processor that predates filtering.
    /// </summary>
    public static string ConfigJson(
        string version,
        params (long ProjectId, string PublicKey, int? RateLimitPerMinute)[] keys) =>
        ConfigJson(version, inboundFilters: null, keys);

    /// <summary>Builds a project config payload including inbound filters.</summary>
    public static string ConfigJson(
        string version,
        IReadOnlyDictionary<long, InboundFilterConfig>? inboundFilters,
        params (long ProjectId, string PublicKey, int? RateLimitPerMinute)[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var projects = keys
            .GroupBy(key => key.ProjectId)
            .Select(group =>
            {
                var project = new Dictionary<string, object?>
                {
                    ["id"] = group.Key,
                    ["keys"] = group
                        .Select(key => new
                        {
                            public_key = key.PublicKey,
                            rate_limit_per_minute = key.RateLimitPerMinute,
                        })
                        .ToArray(),
                };

                if (inboundFilters is not null
                    && inboundFilters.TryGetValue(group.Key, out var filters))
                {
                    project["inbound_filters"] = new
                    {
                        releases = filters.Releases,
                        environments = filters.Environments,
                        message_patterns = filters.MessagePatterns,
                    };
                }

                return project;
            })
            .ToArray();

        return JsonSerializer.Serialize(new { version, projects });
    }

    /// <summary>
    /// Creates a handler that answers every request with the supplied project
    /// config. Used as the default key source of <see cref="RelayApplicationFactory"/>.
    /// </summary>
    public static FakeProjectConfigHandler ForConfig(
        string version,
        params (long ProjectId, string PublicKey, int? RateLimitPerMinute)[] keys) =>
        new(_ => JsonResponse(ConfigJson(version, keys)));

    /// <summary>
    /// Creates a handler that answers every request with a project config that
    /// includes the supplied inbound filters.
    /// </summary>
    public static FakeProjectConfigHandler ForConfig(
        string version,
        IReadOnlyDictionary<long, InboundFilterConfig> inboundFilters,
        params (long ProjectId, string PublicKey, int? RateLimitPerMinute)[] keys) =>
        new(_ => JsonResponse(ConfigJson(version, inboundFilters, keys)));

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var recorded = new RecordedProjectConfigRequest(
            request.RequestUri!,
            request.Method,
            Header(request, "X-GlitchPort-Internal"),
            Header(request, "If-None-Match"));

        Func<HttpRequestMessage, HttpResponseMessage> responder;
        List<TaskCompletionSource> completedWaiters;

        lock (_gate)
        {
            _requests.Add(recorded);
            responder = _responses.Count > 0 ? _responses.Dequeue() : _defaultResponder;

            completedWaiters = [];
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                if (_waiters[index].Count <= _requests.Count)
                {
                    completedWaiters.Add(_waiters[index].Completion);
                    _waiters.RemoveAt(index);
                }
            }
        }

        foreach (var waiter in completedWaiters)
        {
            waiter.TrySetResult();
        }

        try
        {
            return Task.FromResult(responder(request));
        }
        catch (Exception exception)
        {
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private async Task WaitWithTimeoutAsync(Task task, int expectedCount, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
        {
            int observedCount;
            lock (_gate)
            {
                observedCount = _requests.Count;
            }

            throw new TimeoutException(
                $"Expected at least {expectedCount} project config requests but only "
                + $"{observedCount} arrived within {timeout}.");
        }

        await task.ConfigureAwait(false);
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : null;

    private sealed record RequestWaiter(int Count, TaskCompletionSource Completion);
}

/// <summary>Snapshot of one request sent to the fake project config handler.</summary>
internal sealed record RecordedProjectConfigRequest(
    Uri RequestUri,
    HttpMethod Method,
    string? InternalSecret,
    string? IfNoneMatch);

/// <summary>Builders for project config snapshots used by the Relay tests.</summary>
internal static class ProjectConfigTestData
{
    /// <summary>Creates a snapshot from flat (project, key, rate limit) tuples.</summary>
    public static ProjectConfigSnapshot Snapshot(
        string? version,
        params (long ProjectId, string PublicKey, int? RateLimitPerMinute)[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var projects = new Dictionary<long, IReadOnlyDictionary<string, int?>>();

        foreach (var group in keys.GroupBy(key => key.ProjectId))
        {
            projects[group.Key] = group.ToDictionary(
                key => key.PublicKey,
                key => key.RateLimitPerMinute,
                StringComparer.Ordinal);
        }

        return new ProjectConfigSnapshot(version, projects);
    }
}
