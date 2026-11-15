namespace GlitchPort.Processor.Retention;

/// <summary>
/// Configuration of the event retention job, bound from the
/// <c>Processor:Retention</c> configuration section.
/// </summary>
/// <remarks>
/// <see cref="Days"/> may be <c>0</c>, which disables retention entirely.
/// Negative days and non-positive interval or batch values are invalid and
/// fall back to the documented defaults.
/// </remarks>
public sealed class RetentionOptions
{
    /// <summary>Name of the configuration section bound to this type.</summary>
    public const string SectionName = "Retention";

    /// <summary>Number of days events are kept when nothing else is configured.</summary>
    public const int DefaultDays = 30;

    /// <summary>Minutes between two retention runs when nothing else is configured.</summary>
    public const int DefaultIntervalMinutes = 60;

    /// <summary>Rows deleted per batch when nothing else is configured.</summary>
    public const int DefaultBatchSize = 10_000;

    private int _days = DefaultDays;
    private int _intervalMinutes = DefaultIntervalMinutes;
    private int _batchSize = DefaultBatchSize;

    /// <summary>
    /// Number of days events and legacy messages are kept. <c>0</c> disables
    /// retention entirely; a negative value falls back to
    /// <see cref="DefaultDays"/>.
    /// </summary>
    public int Days
    {
        get => _days;
        set => _days = value >= 0 ? value : DefaultDays;
    }

    /// <summary>
    /// Minutes between two retention runs. Non-positive values fall back to
    /// <see cref="DefaultIntervalMinutes"/>.
    /// </summary>
    public int IntervalMinutes
    {
        get => _intervalMinutes;
        set => _intervalMinutes = value > 0 ? value : DefaultIntervalMinutes;
    }

    /// <summary>
    /// Maximum number of events or messages deleted per transaction.
    /// Non-positive values fall back to <see cref="DefaultBatchSize"/>.
    /// </summary>
    public int BatchSize
    {
        get => _batchSize;
        set => _batchSize = value > 0 ? value : DefaultBatchSize;
    }
}
