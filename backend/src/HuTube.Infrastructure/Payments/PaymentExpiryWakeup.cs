namespace HuTube.Infrastructure.Payments;

// The payment row remains the durable source of truth. This signal only wakes
// the local scheduler sooner after a payment is committed.
public sealed class PaymentExpiryWakeup : IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);

    public void Signal()
    {
        try { signal.Release(); }
        catch (SemaphoreFullException) { /* One pending wakeup is enough. */ }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => signal.WaitAsync(timeout, ct);

    public void Dispose() => signal.Dispose();
}
