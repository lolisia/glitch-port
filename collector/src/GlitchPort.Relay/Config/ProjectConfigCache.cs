using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GlitchPort.Relay.Config;

/// <summary>
/// Refreshes the Processor project configuration immediately after startup and
/// then every <c>Relay:ConfigRefreshSeconds</c> seconds. Failed refreshes leave
/// the last good snapshot (and the processor health source) untouched.
/// </summary>
/// <remarks>
/// The Processor can still be starting when the Relay comes up (for example
/// when both containers start together), so the first refresh is retried with a
/// short exponential backoff (1s, 2s, 4s, ... capped at the refresh interval)
/// instead of waiting a full interval while ingest answers <c>503</c>. After the
/// first success the regular cadence starts and a failed refresh only loses that
/// tick: the next attempt happens after the configured interval.
/// </remarks>
public sealed class ProjectConfigCache : BackgroundService
{
    /// <summary>
    /// Highest exponent used by <see cref="GetStartupRetryDelay"/>. 2^32 seconds
    /// is far longer than any accepted refresh interval, and the shift result
    /// stays inside the <see cref="TimeSpan"/> range for any attempt counter.
    /// </summary>
    private const int MaxStartupRetryExponent = 32;

    private readonly ProjectConfigClient _client;
    private readonly ProjectConfigProvider _provider;
    private readonly RelayOptions _options;
    private readonly ILogger<ProjectConfigCache> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the refresh service.</summary>
    /// <param name="client">Client used to fetch the Processor configuration.</param>
    /// <param name="provider">Snapshot holder updated by a successful refresh.</param>
    /// <param name="options">Relay configuration.</param>
    /// <param name="logger">Logger for refresh outcomes.</param>
    /// <param name="timeProvider">
    /// Clock used for the refresh cadence; defaults to the system clock.
    /// </param>
    public ProjectConfigCache(
        ProjectConfigClient client,
        ProjectConfigProvider provider,
        RelayOptions options,
        ILogger<ProjectConfigCache> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _provider = provider;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        RefreshInterval = TimeSpan.FromSeconds(
            Math.Max(RelayOptions.MinimumConfigRefreshSeconds, options.ConfigRefreshSeconds));
    }

    /// <summary>Delay between two refreshes.</summary>
    public TimeSpan RefreshInterval { get; }

    /// <summary>
    /// Returns the delay before the next startup retry: 1s, 2s, 4s, 8s, ...
    /// doubling per failed attempt and capped at <paramref name="refreshInterval"/>.
    /// </summary>
    /// <param name="failedAttempts">
    /// Failures observed since startup; values below one count as one attempt.
    /// </param>
    /// <param name="refreshInterval">
    /// Regular refresh interval (at least one second, see the constructor),
    /// used as the upper bound.
    /// </param>
    internal static TimeSpan GetStartupRetryDelay(int failedAttempts, TimeSpan refreshInterval)
    {
        // Clamp the exponent so int.MaxValue cannot overflow the shift; the
        // clamped value still exceeds any refresh interval accepted from config.
        var exponent = Math.Min(Math.Max(failedAttempts, 1) - 1, MaxStartupRetryExponent);
        var delay = TimeSpan.FromSeconds(1L << exponent);

        return delay < refreshInterval ? delay : refreshInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var failedAttempts = 0;
            while (!await RefreshOnceAsync(stoppingToken).ConfigureAwait(false))
            {
                failedAttempts++;
                var delay = GetStartupRetryDelay(failedAttempts, RefreshInterval);
                LogStartupRetry(failedAttempts, delay);
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(RefreshInterval, _timeProvider);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!await RefreshOnceAsync(stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "Could not refresh project configuration from any Processor endpoint; keeping the last known configuration.");
                }
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
            _logger.LogDebug(
                exception,
                "Unexpected failure while refreshing the Processor project configuration.");
            return false;
        }

        var refreshedAt = _timeProvider.GetUtcNow();

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
                _logger.LogDebug(
                    "Could not refresh project configuration from any Processor endpoint; keeping the last known configuration.");
                return false;
        }
    }

    /// <summary>
    /// Logs a startup retry without flooding the log: at the start and then every
    /// tenth attempt a warning, every other attempt at debug level.
    /// </summary>
    private void LogStartupRetry(int failedAttempts, TimeSpan delay)
    {
        var delaySeconds = delay.TotalSeconds;

        if (failedAttempts == 1 || failedAttempts % 10 == 0)
        {
            _logger.LogWarning(
                "Processor project configuration is not available yet; retrying in {DelaySeconds:0.###}s (startup attempt {Attempt}).",
                delaySeconds,
                failedAttempts);
            return;
        }

        _logger.LogDebug(
            "Processor project configuration is not available yet; retrying in {DelaySeconds:0.###}s (startup attempt {Attempt}).",
            delaySeconds,
            failedAttempts);
    }
}
