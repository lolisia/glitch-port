using Microsoft.Extensions.Options;

namespace GlitchPort.Processor.Retention;

/// <summary>
/// Runs <see cref="RetentionRunner"/> periodically in the background.
/// </summary>
/// <remarks>
/// The first run happens one full <see cref="RetentionOptions.IntervalMinutes"/>
/// after the host started, so a service start (and the test suite) is never
/// delayed by retention work. A failing run is logged and the worker keeps
/// running; only host shutdown stops it.
/// </remarks>
public sealed class RetentionWorker : BackgroundService
{
    private readonly RetentionRunner _runner;
    private readonly RetentionOptions _options;
    private readonly ILogger<RetentionWorker> _logger;

    /// <summary>
    /// Creates the worker from the registered runner and options.
    /// </summary>
    public RetentionWorker(
        RetentionRunner runner,
        IOptions<RetentionOptions> options,
        ILogger<RetentionWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(_options.IntervalMinutes);
        _logger.LogInformation(
            "Retention worker started; the first retention run happens in {IntervalMinutes} minutes.",
            _options.IntervalMinutes);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _runner.RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "The retention run failed; the next run happens in {IntervalMinutes} minutes.",
                        _options.IntervalMinutes);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
