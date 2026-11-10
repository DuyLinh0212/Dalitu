using HuTube.Domain.Payments;
using HuTube.Infrastructure.Payments;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HuTube.UnitTests;

public sealed class PaymentExpirationWorkerTests
{
    [Fact]
    public void NextCheck_UsesDeadlineAndBoundsReconciliation()
    {
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(30), PaymentExpirationWorker.GetDelay(null, now));
        Assert.Equal(TimeSpan.FromMinutes(30), PaymentExpirationWorker.GetDelay(now.AddDays(1), now));
        Assert.Equal(TimeSpan.FromMinutes(3), PaymentExpirationWorker.GetDelay(now.AddMinutes(3), now));
        Assert.Equal(TimeSpan.Zero, PaymentExpirationWorker.GetDelay(now.AddSeconds(-1), now));
    }

    [Fact]
    public async Task Worker_IsWokenForNewPaymentAndCancelsItAfterDeadline()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<HuTubeDbContext>(options => options.UseInMemoryDatabase(databaseName));
        await using var provider = services.BuildServiceProvider();
        using var wakeup = new PaymentExpiryWakeup();
        using var worker = new PaymentExpirationWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, wakeup,
            NullLogger<PaymentExpirationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
                db.Payments.Add(new Payment { PaymentId = id, UserId = Guid.NewGuid(),
                    PlanId = Guid.NewGuid(), Status = "pending", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1) });
                await db.SaveChangesAsync();
            }
            wakeup.Signal();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (true)
            {
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
                var status = await db.Payments.AsNoTracking().Where(payment => payment.PaymentId == id)
                    .Select(payment => payment.Status).SingleAsync(timeout.Token);
                if (status == "cancelled") break;
                await Task.Delay(50, timeout.Token);
            }
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
}
