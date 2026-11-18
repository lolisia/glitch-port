using Microsoft.Extensions.Logging;

namespace GlitchPort.Relay.Tests;

/// <summary>One entry captured by <see cref="RecordingLogger{TCategoryName}"/>.</summary>
internal sealed record RecordedLogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Records every log entry so tests can assert log levels and wait for a
/// specific entry without polling on real time.
/// </summary>
internal sealed class RecordingLogger<TCategoryName> : ILogger<TCategoryName>
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<RecordedLogEntry> _entries = [];
    private readonly List<LogWaiter> _waiters = [];

    /// <summary>Entries logged so far, in order.</summary>
    public IReadOnlyList<RecordedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        var entry = new RecordedLogEntry(logLevel, formatter(state, exception), exception);
        List<TaskCompletionSource<RecordedLogEntry>> completed = [];

        lock (_gate)
        {
            _entries.Add(entry);

            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                if (!_waiters[index].Predicate(entry))
                {
                    continue;
                }

                completed.Add(_waiters[index].Completion);
                _waiters.RemoveAt(index);
            }
        }

        foreach (var completion in completed)
        {
            completion.TrySetResult(entry);
        }
    }

    /// <summary>
    /// Waits until an entry matching <paramref name="predicate"/> is logged and
    /// returns it. Entries logged before the call are matched as well.
    /// </summary>
    public async Task<RecordedLogEntry> WaitForEntryAsync(Func<RecordedLogEntry, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        Task<RecordedLogEntry> task;

        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(predicate);
            if (existing is not null)
            {
                return existing;
            }

            var completion = new TaskCompletionSource<RecordedLogEntry>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new LogWaiter(predicate, completion));
            task = completion.Task;
        }

        var timeout = Task.Delay(WaitTimeout);
        if (await Task.WhenAny(task, timeout).ConfigureAwait(false) != task)
        {
            throw new TimeoutException("The expected log entry was not written in time.");
        }

        return await task.ConfigureAwait(false);
    }

    private sealed record LogWaiter(
        Func<RecordedLogEntry, bool> Predicate,
        TaskCompletionSource<RecordedLogEntry> Completion);
}
