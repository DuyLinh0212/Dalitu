using HuTube.Domain.Payments;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Payments;

public sealed class PaymentExpirationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    PaymentExpiryWakeup wakeup,
    ILogger<PaymentExpirationWorker> logger) : BackgroundService
{
    // Reconcile infrequently in case another instance commits a payment and
    // stops before its local scheduler observes it.
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ErrorRetryInterval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ErrorRetryInterval;
            try
            {
                var nextExpiry = await ExpirePaymentsAndFindNextAsync(stoppingToken);
                delay = GetDelay(nextExpiry, clock.GetUtcNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to expire pending payments.");
            }

            await wakeup.WaitAsync(delay, stoppingToken);
        }
    }

    internal static TimeSpan GetDelay(DateTimeOffset? nextExpiry, DateTimeOffset now)
    {
        var recoveryDelay = BackgroundWorkSchedule.UntilNextBoundary(ReconciliationInterval, now);
        if (!nextExpiry.HasValue) return recoveryDelay;
        var untilExpiry = nextExpiry.Value - now;
        if (untilExpiry <= TimeSpan.Zero) return TimeSpan.Zero;
        return untilExpiry < recoveryDelay ? untilExpiry : recoveryDelay;
    }

    private async Task<DateTimeOffset?> ExpirePaymentsAndFindNextAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
        var now = clock.GetUtcNow();
        var expired = await db.Payments.AsNoTracking()
            .Where(payment => payment.Status == "pending" && payment.ExpiresAt <= now)
            .OrderBy(payment => payment.ExpiresAt)
            .Select(payment => new { payment.PaymentId, payment.UserId })
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var candidate in expired)
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct) : null;
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({"plan-membership:" + candidate.UserId}, 0))", ct);

            var payment = await db.Payments.SingleOrDefaultAsync(item => item.PaymentId == candidate.PaymentId, ct);
            if (payment is { Status: "pending" } && payment.ExpiresAt <= now)
            {
                payment.Status = "cancelled";
                payment.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }

            if (transaction != null) await transaction.CommitAsync(ct);
        }

        // Continue immediately when there may be more than one batch due.
        if (expired.Count == BatchSize) return now;
        return await db.Payments.AsNoTracking()
            .Where(payment => payment.Status == "pending")
            .Select(payment => (DateTimeOffset?)payment.ExpiresAt)
            .MinAsync(ct);
    }
}
