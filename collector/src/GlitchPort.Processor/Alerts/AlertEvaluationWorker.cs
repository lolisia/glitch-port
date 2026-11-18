using Microsoft.Extensions.Options;

namespace GlitchPort.Processor.Alerts;

/// <summary>
/// Runs <see cref="AlertEvaluator"/> periodically in the background.
/// </summary>
/// <remarks>
/// The first run happens one full <see cref="AlertOptions.EvaluateIntervalSeconds"/>
/// after the host started, so a service start (and the test suite) is never
/// delayed by evaluation work. A disabled worker exits immediately without
/// holding the advisory lock; queueing new notifications is not affected. A
/// failing run is logged and the worker keeps running; only host shutdown
/// stops it.
/// </remarks>
public sealed class AlertEvaluationWorker : BackgroundService
{
    private readonly AlertEvaluator _evaluator;
    private readonly AlertOptions _options;
    private readonly ILogger<AlertEvaluationWorker> _logger;

    /// <summary>
    /// Creates the worker from the registered evaluator and options.
    /// </summary>
    public AlertEvaluationWorker(
        AlertEvaluator evaluator,
        IOptions<AlertOptions> options,
        ILogger<AlertEvaluationWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _evaluator = evaluator;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Alert evaluation is disabled; the worker does not start.");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.EvaluateIntervalSeconds);
        _logger.LogInformation(
            "Alert evaluation worker started; the first evaluation happens in {IntervalSeconds} seconds.",
            _options.EvaluateIntervalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _evaluator.EvaluateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "The alert evaluation failed; the next evaluation happens in {IntervalSeconds} seconds.",
                        _options.EvaluateIntervalSeconds);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
