using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GlitchPort.Relay.Config;

/// <summary>
/// Refreshes the Processor project configuration immediately after startup and
/// then every <c>Relay:ConfigRefreshSeconds</c> seconds. Failed refreshes leave
/// the last good snapshot (and the processor health source) untouched.
/// </summary>
public sealed class ProjectConfigCache : BackgroundService
{
    private readonly ProjectConfigClient _client;
    private readonly ProjectConfigProvider _provider;
    private readonly RelayOptions _options;
    private readonly ILogger<ProjectConfigCache> _logger;

    /// <summary>Creates the refresh service.</summary>
    public ProjectConfigCache(
        ProjectConfigClient client,
        ProjectConfigProvider provider,
        RelayOptions options,
        ILogger<ProjectConfigCache> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _provider = provider;
        _options = options;
        _logger = logger;
        RefreshInterval = TimeSpan.FromSeconds(
            Math.Max(RelayOptions.MinimumConfigRefreshSeconds, options.ConfigRefreshSeconds));
    }

    /// <summary>Delay between two refreshes.</summary>
    public TimeSpan RefreshInterval { get; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(RefreshInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one refresh. Returns <see langword="true"/> when a Processor
    /// returned <c>200</c> or <c>304</c>.
    /// </summary>
    public async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var knownVersion = _provider.Snapshot.Version;

        ProjectConfigFetchResult result;
        try
        {
            result = await _client
                .FetchAsync(
                    _options.ProcessorEndpoints,
                    _options.InternalSharedSecret,
                    knownVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Unexpected failure while refreshing the Processor project configuration.");
            return false;
        }

        var refreshedAt = DateTimeOffset.UtcNow;

        switch (result.Status)
        {
            case ProjectConfigFetchStatus.Success when result.Snapshot is not null:
                _provider.ApplySnapshot(result.Snapshot, refreshedAt);
                _logger.LogInformation(
                    "Loaded Processor project configuration with {ProjectCount} projects.",
                    result.Snapshot.ProjectCount);
                return true;

            case ProjectConfigFetchStatus.NotModified:
                _provider.MarkRefreshed(refreshedAt);
                return true;

            default:
                _logger.LogWarning(
                    "Could not refresh project configuration from any Processor endpoint; keeping the last known configuration.");
                return false;
        }
    }
}
