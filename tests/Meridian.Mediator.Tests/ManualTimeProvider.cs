namespace Meridian.Mediator.Tests;

// Only one-shot timers are used by Task.Delay and CancellationTokenSource in these tests.
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object sync = new();
    private DateTimeOffset now = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> timers = [];
    private int scheduled;
    public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (sync)
        {
            var timer = new ManualTimer(this, callback, state);
            scheduled++; timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
    }
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
    }
    internal async Task WaitForScheduledCount(int count)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            lock (sync) if (scheduled >= count) return;
            await Task.Delay(1, watchdog.Token);
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
