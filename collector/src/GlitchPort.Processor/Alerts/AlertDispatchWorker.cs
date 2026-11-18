using Microsoft.Extensions.Options;

namespace GlitchPort.Processor.Alerts;

/// <summary>
/// Runs <see cref="AlertDispatcher"/> periodically in the background.
/// </summary>
/// <remarks>
/// Every Processor instance runs its own dispatch loop; the claim of
/// <see cref="AlertDispatcher"/> makes sure a notification is sent once even
/// when several instances run at the same time. A disabled worker exits
/// immediately without claiming anything. A failing run is logged and the
/// worker keeps running; only host shutdown stops it.
/// </remarks>
public sealed class AlertDispatchWorker : BackgroundService
{
    private readonly AlertDispatcher _dispatcher;
    private readonly AlertOptions _options;
    private readonly ILogger<AlertDispatchWorker> _logger;

    /// <summary>
    /// Creates the worker from the registered dispatcher and options.
    /// </summary>
    public AlertDispatchWorker(
        AlertDispatcher dispatcher,
        IOptions<AlertOptions> options,
        ILogger<AlertDispatchWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Alert dispatch is disabled; the worker does not start.");
            return;
        }

        _logger.LogInformation(
            "Alert dispatch worker started; it runs every {IntervalSeconds} seconds.",
            _options.DispatchIntervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.DispatchIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _dispatcher.DispatchAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "The alert dispatch failed; the next run happens in {IntervalSeconds} seconds.",
                        _options.DispatchIntervalSeconds);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
