using HuTube.Application.Serialization;
using HuTube.Domain.Rbac;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Videos;

public sealed class ChannelSuspensionExpirationWorker(
    IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ChannelSuspensionExpirationWorker> logger,
    BackgroundWorkWakeup<ChannelSuspensionExpirationWorker>? wakeup = null) : BackgroundService
{
    private readonly BackgroundWorkWakeup<ChannelSuspensionExpirationWorker> scheduler = wakeup ?? new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(30);
                try
                {
                    await RestoreExpiredChannelsAsync(stoppingToken);
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
                    var next = await db.Channels.AsNoTracking()
                        .Where(channel => channel.Status == "suspended" && channel.SuspendedUntil.HasValue)
                        .Select(channel => channel.SuspendedUntil).MinAsync(stoppingToken);
                    var now = clock.GetUtcNow();
                    delay = next <= now ? TimeSpan.FromMinutes(1)
                        : BackgroundWorkWakeup<ChannelSuspensionExpirationWorker>.DelayUntil(next, now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Không thể mở lại kênh đã hết hạn tạm ngưng.");
                }
                await scheduler.WaitAsync(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task RestoreExpiredChannelsAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
        var now = clock.GetUtcNow();
        while (true)
        {
            var due = await db.Channels.AsNoTracking()
                .Where(c => c.Status == "suspended" && c.SuspendedUntil.HasValue && c.SuspendedUntil <= now)
                .OrderBy(c => c.SuspendedUntil).ThenBy(c => c.ChannelId)
                .Select(c => new { c.ChannelId, c.SuspendedUntil })
                .Take(100).ToListAsync(ct);

            foreach (var channel in due)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                // Claim only this suspension. A later ban, deletion or extended deadline
                // must win, and multiple API instances must not restore/audit twice.
                var changed = await db.Channels
                    .Where(c => c.ChannelId == channel.ChannelId && c.Status == "suspended"
                        && c.SuspendedUntil == channel.SuspendedUntil && c.SuspendedUntil <= now)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(c => c.Status, "active")
                        .SetProperty(c => c.SuspendedUntil, (DateTimeOffset?)null)
                        .SetProperty(c => c.StatusReason, (string?)null)
                        .SetProperty(c => c.UpdatedAt, now), ct);
                if (changed == 0) continue;
                db.AuditLogs.Add(new AuditLog
                {
                    Action = "channel.suspension_expired", ResourceType = "channel", ResourceId = channel.ChannelId,
                    Reason = "Tự động mở lại kênh khi hết thời gian tạm ngưng.", CreatedAt = now,
                    OldValues = PersistenceJson.Serialize(new { status = "suspended", suspendedUntil = channel.SuspendedUntil }),
                    NewValues = PersistenceJson.Serialize(new { status = "active" })
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            if (due.Count < 100) return;
        }
    }
}
