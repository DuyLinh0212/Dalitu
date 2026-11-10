namespace HuTube.Infrastructure.Persistence;

public static class BackgroundWorkSchedule
{
    public static TimeSpan UntilNextBoundary(TimeSpan interval, DateTimeOffset now)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        var utcTicks = now.UtcDateTime.Ticks;
        var next = new DateTimeOffset((utcTicks / interval.Ticks + 1) * interval.Ticks, TimeSpan.Zero);
        return next - now;
    }
}

// Database rows remain the durable queue. A local signal starts work promptly;
// aligned reconciliation also recovers a commit whose process stopped before signaling.
public sealed class BackgroundWorkWakeup<T> : IDisposable
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim signal;
    private readonly int capacity;
    private readonly object gate = new();

    public BackgroundWorkWakeup(int capacity = 1)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        signal = new SemaphoreSlim(0, capacity);
    }

    public void Signal()
    {
        lock (gate)
        {
            var missing = capacity - signal.CurrentCount;
            if (missing > 0) signal.Release(missing);
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => signal.WaitAsync(timeout, ct);

    public static TimeSpan DelayUntil(DateTimeOffset? dueAt, DateTimeOffset now)
    {
        var delay = BackgroundWorkSchedule.UntilNextBoundary(ReconciliationInterval, now);
        if (dueAt.HasValue && dueAt.Value - now < delay) delay = dueAt.Value - now;
        // A row can be locked by another worker; avoid spinning on an overdue row.
        return delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
    }

    public void Dispose() => signal.Dispose();
}
