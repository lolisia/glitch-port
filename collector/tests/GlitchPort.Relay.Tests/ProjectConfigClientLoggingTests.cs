using System.Net;
using System.Text.Json;
using GlitchPort.Relay.Config;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GlitchPort.Relay.Tests;

/// <summary>
/// Proves that the project-config client keeps every per-endpoint failure
/// visible at debug level and never warns on its own. The refresh cache owns
/// the warning cadence, so a startup retry cannot emit one warning per attempt.
/// </summary>
public sealed class ProjectConfigClientLoggingTests
{
    private const string Endpoint = "http://processor-a:5090";
    private const string EndpointLabel = "processor-a:5090";

    [Fact]
    public async Task FetchAsync_WhenEndpointFails_LogsAtDebugWithoutWarning()
    {
        await AssertFailureIsDebugAsync(
            handler => handler.Enqueue(_ => throw new HttpRequestException("connection refused")),
            "Failed to fetch project configuration from " + EndpointLabel,
            typeof(HttpRequestException));

        await AssertFailureIsDebugAsync(
            handler => handler.EnqueueStatus(HttpStatusCode.InternalServerError),
            "returned HTTP 500");

        await AssertFailureIsDebugAsync(
            handler => handler.EnqueueJson("{ this is not valid json"),
            "returned invalid JSON",
            typeof(JsonException));

        await AssertFailureIsDebugAsync(
            handler => handler.EnqueueOversizedResponse(),
            "byte limit");
    }

    [Fact]
    public async Task FetchAsync_WithoutConfiguredEndpoints_LogsAtDebugWithoutWarning()
    {
        using var handler = new FakeProjectConfigHandler();
        var logger = new RecordingLogger<ProjectConfigClient>();
        using var httpClient = new HttpClient(handler);
        var client = new ProjectConfigClient(httpClient, logger);

        var result = await client.FetchAsync(
            [],
            "internal-secret",
            knownVersion: null,
            CancellationToken.None);

        Assert.Equal(ProjectConfigFetchStatus.Failed, result.Status);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains(
            "No Processor endpoints are configured",
            entry.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs one failed fetch, then asserts it produced exactly one debug entry
    /// carrying the endpoint and (when applicable) the exception, and no entry
    /// at warning level or above.
    /// </summary>
    private static async Task AssertFailureIsDebugAsync(
        Action<FakeProjectConfigHandler> arrange,
        string expectedMessage,
        Type? expectedException = null)
    {
        ArgumentNullException.ThrowIfNull(arrange);

        using var handler = new FakeProjectConfigHandler();
        arrange(handler);
        var logger = new RecordingLogger<ProjectConfigClient>();
        using var httpClient = new HttpClient(handler);
        var client = new ProjectConfigClient(httpClient, logger);

        var result = await client.FetchAsync(
            [Endpoint],
            "internal-secret",
            knownVersion: null,
            CancellationToken.None);

        Assert.Equal(ProjectConfigFetchStatus.Failed, result.Status);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains(expectedMessage, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            logger.Entries,
            candidate => candidate.Level >= LogLevel.Warning);

        if (expectedException is null)
        {
            Assert.Null(entry.Exception);
            return;
        }

        Assert.NotNull(entry.Exception);
        Assert.IsType(expectedException, entry.Exception);
    }
}
