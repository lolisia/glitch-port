namespace GlitchPort.Processor.Alerts;

/// <summary>
/// Configuration of the alert pipeline, bound from the
/// <c>Processor:Alerts</c> configuration section.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> only controls the evaluation and dispatch workers.
/// Queueing notifications always happens, so enabling alerts later still
/// delivers the backlog. Non-positive intervals, attempt or timeout values
/// fall back to the documented defaults instead of breaking a worker.
/// </remarks>
public sealed class AlertOptions
{
    /// <summary>Name of the configuration section bound to this type.</summary>
    public const string SectionName = "Alerts";

    /// <summary>Seconds between two event-frequency evaluations when nothing else is configured.</summary>
    public const int DefaultEvaluateIntervalSeconds = 60;

    /// <summary>Seconds between two dispatch runs when nothing else is configured.</summary>
    public const int DefaultDispatchIntervalSeconds = 5;

    /// <summary>Delivery attempts per notification when nothing else is configured.</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>HTTP timeout of a delivery attempt when nothing else is configured.</summary>
    public const int DefaultHttpTimeoutSeconds = 10;

    /// <summary>Days sent and failed notifications are kept when nothing else is configured.</summary>
    public const int DefaultRetentionDays = 30;

    private int _evaluateIntervalSeconds = DefaultEvaluateIntervalSeconds;
    private int _dispatchIntervalSeconds = DefaultDispatchIntervalSeconds;
    private int _maxAttempts = DefaultMaxAttempts;
    private int _httpTimeoutSeconds = DefaultHttpTimeoutSeconds;
    private int _retentionDays = DefaultRetentionDays;

    /// <summary>
    /// Whether the evaluation and dispatch workers run. Queueing notifications
    /// is not affected, so a disabled Processor keeps building the backlog.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between two event-frequency evaluations. Non-positive values
    /// fall back to <see cref="DefaultEvaluateIntervalSeconds"/>.
    /// </summary>
    public int EvaluateIntervalSeconds
    {
        get => _evaluateIntervalSeconds;
        set => _evaluateIntervalSeconds =
            value > 0 ? value : DefaultEvaluateIntervalSeconds;
    }

    /// <summary>
    /// Seconds between two dispatch runs. Non-positive values fall back to
    /// <see cref="DefaultDispatchIntervalSeconds"/>.
    /// </summary>
    public int DispatchIntervalSeconds
    {
        get => _dispatchIntervalSeconds;
        set => _dispatchIntervalSeconds =
            value > 0 ? value : DefaultDispatchIntervalSeconds;
    }

    /// <summary>
    /// Delivery attempts per notification before it is marked failed.
    /// Non-positive values fall back to <see cref="DefaultMaxAttempts"/>.
    /// </summary>
    public int MaxAttempts
    {
        get => _maxAttempts;
        set => _maxAttempts = value > 0 ? value : DefaultMaxAttempts;
    }

    /// <summary>
    /// HTTP timeout of one delivery attempt in seconds. Non-positive values
    /// fall back to <see cref="DefaultHttpTimeoutSeconds"/>.
    /// </summary>
    public int HttpTimeoutSeconds
    {
        get => _httpTimeoutSeconds;
        set => _httpTimeoutSeconds =
            value > 0 ? value : DefaultHttpTimeoutSeconds;
    }

    /// <summary>
    /// Base URL of the dashboard used to build issue links in notifications.
    /// An empty value stores <c>null</c> as the link.
    /// </summary>
    public string DashboardBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Days sent and failed notifications are kept before the evaluator
    /// deletes them. <c>0</c> disables the cleanup; negative values fall back
    /// to <see cref="DefaultRetentionDays"/>.
    /// </summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set => _retentionDays = value >= 0 ? value : DefaultRetentionDays;
    }
}
