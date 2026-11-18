namespace GlitchPort.Relay.Tests;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> that only moves when a test asks it
/// to. Timers created through it (for example by <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// or <see cref="PeriodicTimer"/>) run in the same fake time, so refresh
/// cadences can be exercised without real waits.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    /// <summary>Upper bound for <see cref="WaitForTimerAsync"/> so a bug fails instead of hanging.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly List<TimerWaiter> _waiters = [];
    private DateTimeOffset _utcNow;
    private long _sequence;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    /// <summary>Current UTC time reported by the clock.</summary>
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new ManualTimer(this, callback, state);

        lock (_gate)
        {
            _timers.Add(timer);
            ArmLocked(timer, dueTime, period);
            NotifyWaitersLocked();
        }

        return timer;
    }

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Fake time cannot move backwards.");
        }

        lock (_gate)
        {
            _utcNow += duration;
        }

        while (TakeDueTimer() is { } timer)
        {
            timer.Fire();
        }
    }

    /// <summary>
    /// Waits until a timer is armed with exactly <paramref name="dueIn"/>
    /// remaining. Tests use this before advancing the clock so they cannot race
    /// the code under test scheduling its next refresh.
    /// </summary>
    public Task WaitForTimerAsync(TimeSpan dueIn) => WaitAsync(dueIn);

    private async Task WaitAsync(TimeSpan? dueIn)
    {
        Task task;

        lock (_gate)
        {
            if (IsSatisfiedLocked(dueIn))
            {
                return;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new TimerWaiter(dueIn, completion));
            task = completion.Task;
        }

        var timeout = Task.Delay(WaitTimeout);
        if (await Task.WhenAny(task, timeout).ConfigureAwait(false) != task)
        {
            throw new TimeoutException(
                "The code under test did not schedule the expected timer in fake time.");
        }

        await task.ConfigureAwait(false);
    }

    private bool IsSatisfiedLocked(TimeSpan? dueIn)
    {
        foreach (var timer in _timers)
        {
            if (timer.DueTicks is not long dueTicks)
            {
                continue;
            }

            if (dueIn is null || dueTicks == _utcNow.UtcTicks + dueIn.Value.Ticks)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes and returns the earliest timer that became due, re-arming it when
    /// it is periodic. The callback itself is invoked outside the lock.
    /// </summary>
    private ManualTimer? TakeDueTimer()
    {
        ManualTimer? due = null;

        lock (_gate)
        {
            foreach (var timer in _timers)
            {
                if (timer.DueTicks is not long dueTicks || dueTicks > _utcNow.UtcTicks)
                {
                    continue;
                }

                if (due is null
                    || timer.DueTicks < due.DueTicks
                    || (timer.DueTicks == due.DueTicks && timer.Sequence < due.Sequence))
                {
                    due = timer;
                }
            }

            due?.OnFiredLocked();
        }

        return due;
    }

    /// <summary>Programs <paramref name="timer"/> relative to the current fake time.</summary>
    private void ArmLocked(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        timer.Sequence = NextSequenceLocked();

        if (dueTime == Timeout.InfiniteTimeSpan)
        {
            timer.DueTicks = null;
            timer.PeriodTicks = null;
            return;
        }

        if (dueTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dueTime),
                dueTime,
                "A timer due time cannot be negative.");
        }

        timer.DueTicks = _utcNow.UtcTicks + dueTime.Ticks;
        timer.PeriodTicks = period < TimeSpan.Zero ? null : period.Ticks;
    }

    private void NotifyWaitersLocked()
    {
        for (var index = _waiters.Count - 1; index >= 0; index--)
        {
            if (!IsSatisfiedLocked(_waiters[index].DueIn))
            {
                continue;
            }

            var completion = _waiters[index].Completion;
            _waiters.RemoveAt(index);
            completion.TrySetResult();
        }
    }

    private long NextSequenceLocked() => ++_sequence;

    private sealed record TimerWaiter(TimeSpan? DueIn, TaskCompletionSource Completion);

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
        }

        /// <summary>Absolute due time in UTC ticks; null while disarmed or disposed.</summary>
        public long? DueTicks { get; set; }

        /// <summary>Period in ticks for a periodic timer; null for a one-shot timer.</summary>
        public long? PeriodTicks { get; set; }

        /// <summary>Arm order, used to fire timers that share a due time in order.</summary>
        public long Sequence { get; set; }

        private bool IsDisposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_provider._gate)
            {
                if (IsDisposed)
                {
                    return false;
                }

                _provider.ArmLocked(this, dueTime, period);
                _provider.NotifyWaitersLocked();
                return true;
            }
        }

        public void Dispose()
        {
            lock (_provider._gate)
            {
                if (IsDisposed)
                {
                    return;
                }

                IsDisposed = true;
                DueTicks = null;
                PeriodTicks = null;
                _provider._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire() => _callback(_state);

        /// <summary>Disarms a one-shot timer or re-arms a periodic one.</summary>
        public void OnFiredLocked()
        {
            if (PeriodTicks is long periodTicks && periodTicks > 0)
            {
                Sequence = _provider.NextSequenceLocked();
                DueTicks = _provider._utcNow.UtcTicks + periodTicks;
                _provider.NotifyWaitersLocked();
                return;
            }

            DueTicks = null;
            PeriodTicks = null;
        }
    }
}
