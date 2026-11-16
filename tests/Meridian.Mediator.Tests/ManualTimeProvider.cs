namespace Meridian.Mediator.Tests;

// Only one-shot timers are used by Task.Delay and CancellationTokenSource in these tests.
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object sync = new();
    private DateTimeOffset now = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> timers = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> waiters = [];
    private int scheduled;
    private int lastScheduledThread;
    private int advanced;
    public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
    /// <summary>Total number of timers ever created through this clock (Task.Delay and CancellationTokenSource timeouts).</summary>
    internal int ScheduledCount { get { lock (sync) return scheduled; } }
    /// <summary>Number of completed <see cref="Advance"/> calls.</summary>
    internal int AdvanceCount => Volatile.Read(ref advanced);
    /// <summary>Managed thread that created the most recent timer; lets a test prove which thread ran a continuation.</summary>
    internal int LastScheduledThread { get { lock (sync) return lastScheduledThread; } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        List<TaskCompletionSource>? ready = null;
        ManualTimer timer;
        lock (sync)
        {
            timer = new ManualTimer(this, callback, state);
            scheduled++; lastScheduledThread = Environment.CurrentManagedThreadId; timers.Add(timer); timer.Change(dueTime, period);
            foreach (var waiter in waiters.Where(w => w.Count <= scheduled).ToList())
            {
                (ready ??= []).Add(waiter.Signal); waiters.Remove(waiter);
            }
        }
        // Signalled outside the lock; the signals also run their continuations asynchronously.
        if (ready is not null) foreach (var signal in ready) signal.TrySetResult();
        return timer;
    }
    /// <summary>
    /// Moves the clock and fires every due timer on the calling thread, in creation order, before returning. A fired
    /// CancellationTokenSource timeout therefore also runs the callbacks registered on its token (for example a pending
    /// Task.Delay and the Task.WhenAny waiting on it) before Advance returns.
    /// </summary>
    internal void Advance(TimeSpan duration)
    {
        List<ManualTimer> due;
        lock (sync)
        {
            now += duration;
            due = timers.Where(t => t.Due <= now).ToList();
            foreach (var timer in due) timer.Due = DateTimeOffset.MaxValue;
        }
        foreach (var timer in due) timer.Fire();
        Interlocked.Increment(ref advanced);
    }
    /// <summary>
    /// Completes when at least <paramref name="count"/> timers have been created. Event driven (no polling); the caller
    /// bounds the wait with its own watchdog.
    /// </summary>
    internal Task WaitForScheduledCount(int count)
    {
        lock (sync)
        {
            if (scheduled >= count) return Task.CompletedTask;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((count, signal));
            return signal.Task;
        }
    }
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal DateTimeOffset Due = DateTimeOffset.MaxValue;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot test timers are supported.");
            lock (owner.sync)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime;
                return true;
            }
        }
        internal void Fire() { lock (owner.sync) { if (disposed) return; } callback(state); }
        public void Dispose() { lock (owner.sync) { disposed = true; owner.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }
}
