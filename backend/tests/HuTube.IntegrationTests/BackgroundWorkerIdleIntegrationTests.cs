using System.Collections.Concurrent;
using System.Data.Common;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Notifications;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HuTube.IntegrationTests;

public sealed class BackgroundWorkerIdleIntegrationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 1, 0, 10, 0, TimeSpan.Zero);
    }

    private sealed class Publisher : IModerationEventPublisher
    {
        public ConcurrentQueue<Guid> Published { get; } = new();
        public Task PublishAsync(ModerationOutboxEvent message, CancellationToken ct)
        { Published.Enqueue(message.EventId); return Task.CompletedTask; }
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        private int count;
        public int Count => Volatile.Read(ref count);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("moderation_outbox_events", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task IdleRecommendationAndBackupWorkersReleaseTheirLeadershipLocks()
    {
        using var client = factory.CreateClient();
        (await client.GetAsync("/health/live")).EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (await LeadershipLocksAsync(timeout.Token) != 0)
            await Task.Delay(50, timeout.Token);

        await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
        Assert.Equal(0, await LeadershipLocksAsync(timeout.Token));
    }

    private async Task<long> LeadershipLocksAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(factory.Connection);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_locks
            WHERE locktype = 'advisory' AND granted AND objsubid = 1
              AND ((classid::bigint << 32) | objid::bigint) IN
                  (hashtextextended('hutube:recommendation-worker', 0),
                   hashtextextended('hutube:database-backup-worker', 0))
            """, connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task EmptyOutboxDoesNotPollAndCommittedWorkWakesPublisher()
    {
        var clock = new Clock();
        var publisher = new Publisher();
        var counter = new QueryCounter();
        var services = new ServiceCollection();
        services.AddDbContext<HuTubeDbContext>(options => options.UseNpgsql(factory.Connection).AddInterceptors(counter));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IModerationEventPublisher>(publisher);
        services.AddScoped<ModerationOutboxPublisher>();
        await using var provider = services.BuildServiceProvider();
        using var wakeup = new BackgroundWorkWakeup<ModerationOutboxWorker>();
        using var worker = new ModerationOutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            new RabbitMqOptions { Enabled = true }, wakeup, clock, NullLogger<ModerationOutboxWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => counter.Count >= 2);
            var idleQueries = counter.Count;
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Assert.Equal(idleQueries, counter.Count);

            var eventId = Guid.NewGuid();
            await using (var db = factory.CreateDb())
            {
                db.ModerationOutboxEvents.Add(new ModerationOutboxEvent { EventId = eventId,
                    EventType = "test", Payload = "{}", OccurredAt = clock.GetUtcNow(), AvailableAt = clock.GetUtcNow() });
                await db.SaveChangesAsync();
            }
            wakeup.Signal();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                await using var check = factory.CreateDb();
                var publishedAt = await check.ModerationOutboxEvents.AsNoTracking()
                    .Where(row => row.EventId == eventId).Select(row => row.PublishedAt).SingleAsync(timeout.Token);
                if (publishedAt.HasValue) break;
                await Task.Delay(25, timeout.Token);
            }
            Assert.Contains(eventId, publisher.Published);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}
