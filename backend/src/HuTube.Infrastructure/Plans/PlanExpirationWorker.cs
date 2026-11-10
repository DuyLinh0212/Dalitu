using HuTube.Domain.Channels;
using HuTube.Domain.Plans;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HuTube.Infrastructure.Plans;

/// <summary>
/// Expires subscription histories and moves affected accounts to the configured default plan.
/// </summary>
public sealed class PlanExpirationWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    BackgroundWorkWakeup<PlanExpirationWorker> wakeup,
    ILogger<PlanExpirationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(30);
                try
                {
                    if (await ProcessExpiredSubscriptionsSafelyAsync(stoppingToken))
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
                        var next = await db.PlanHistories.AsNoTracking()
                            .Where(history => history.Status == "active" && history.EndedAt.HasValue)
                            .Select(history => history.EndedAt).MinAsync(stoppingToken);
                        var now = clock.GetUtcNow();
                        delay = next <= now ? Interval : BackgroundWorkWakeup<PlanExpirationWorker>.DelayUntil(next, now);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogError(ex, "Không thể lên lịch kiểm tra gói hết hạn."); }
                await wakeup.WaitAsync(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> ProcessExpiredSubscriptionsSafelyAsync(CancellationToken ct)
    {
        try
        {
            await ProcessExpiredSubscriptionsAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Không thể chuyển tài khoản có gói hết hạn về gói mặc định.");
            return false;
        }
    }

    private async Task ProcessExpiredSubscriptionsAsync(CancellationToken ct)
    {
        await using var lockScope = scopes.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
        await using var lockConnection = new NpgsqlConnection(DatabaseConnectionString.ForSessionLocks(
            lockDb.Database.GetConnectionString() ?? throw new InvalidOperationException("Missing database connection.")));
        await lockConnection.OpenAsync(ct);
        var acquiredLock = false;
        try
        {
            await using var command = lockConnection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(hashtextextended('hutube:plan-expiration-worker', 0))";
            acquiredLock = (bool)(await command.ExecuteScalarAsync(ct))!;
            if (!acquiredLock) return;

            await ProcessExpiredBatchAsync(ct);
        }
        finally
        {
            if (acquiredLock)
            {
                try
                {
                    await using var command = lockConnection.CreateCommand();
                    command.CommandText = "SELECT pg_advisory_unlock(hashtextextended('hutube:plan-expiration-worker', 0))";
                    await command.ExecuteScalarAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Không thể nhả khóa worker hết hạn gói; PostgreSQL sẽ nhả khóa khi kết nối đóng.");
                }
            }

            await lockConnection.CloseAsync();
        }
    }

    private async Task ProcessExpiredBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
        var now = clock.GetUtcNow();
        var expiredHistoryIds = await db.PlanHistories.AsNoTracking()
            .Where(history => history.Status == "active" && history.EndedAt.HasValue && history.EndedAt <= now)
            .OrderBy(history => history.EndedAt)
            .Select(history => history.PlanHistoryId)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var historyId in expiredHistoryIds)
        {
            try
            {
                await ExpireSubscriptionAsync(db, historyId, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Không thể xử lý gói hết hạn {PlanHistoryId}.", historyId);
            }
        }
    }

    private async Task ExpireSubscriptionAsync(HuTubeDbContext db, Guid historyId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var history = await db.PlanHistories.AsNoTracking()
            .Where(item => item.PlanHistoryId == historyId)
            .Select(item => new { item.UserId, item.PlanId })
            .SingleOrDefaultAsync(ct);
        if (history == null) return;

        var defaultPlan = await FindDefaultPlanAsync(db, ct);
        if (defaultPlan == null)
        {
            logger.LogError("Không thể chuyển gói hết hạn {PlanHistoryId}: chưa có gói mặc định hoặc gói free đang hoạt động.", historyId);
            return;
        }

        var memberUserIds = await db.PlanMembers.AsNoTracking()
            .Where(member => member.PlanHistoryId == historyId && member.Status == "accepted" && member.MemberUserId.HasValue)
            .Select(member => member.MemberUserId!.Value)
            .ToListAsync(ct);
        foreach (var userId in memberUserIds.Append(history.UserId).Distinct().Order())
            await LockMembershipAsync(db, userId, ct);

        // The conditional update is the claim: if another API instance already processed
        // this history, its affected-row count is zero and no duplicate default history is added.
        var claimed = await db.PlanHistories
            .Where(item => item.PlanHistoryId == historyId && item.Status == "active"
                && item.EndedAt.HasValue && item.EndedAt <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "expired"), ct);
        if (claimed == 0) return;

        var affectedUserIds = memberUserIds.Append(history.UserId).Distinct().ToArray();

        foreach (var userId in affectedUserIds)
        {
            var user = await db.Users.SingleOrDefaultAsync(item => item.UserId == userId, ct);
            if (user == null || (user.PlanId.HasValue && user.PlanId != history.PlanId)) continue;

            var hasActiveOwnedPlan = await db.PlanHistories.AnyAsync(item => item.UserId == userId
                && item.Status == "active" && (!item.EndedAt.HasValue || item.EndedAt > now), ct);
            if (hasActiveOwnedPlan && !memberUserIds.Contains(userId)) continue;
            if (memberUserIds.Contains(userId))
            {
                await db.PlanHistories.Where(item => item.UserId == userId && item.Status == "active")
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.Status, "expired")
                        .SetProperty(item => item.EndedAt, now)
                        .SetProperty(item => item.AutoRenew, false), ct);
            }

            var hasActiveSharedPlan = await (from member in db.PlanMembers.AsNoTracking()
                                             join activeHistory in db.PlanHistories.AsNoTracking()
                                                 on member.PlanHistoryId equals activeHistory.PlanHistoryId
                                             where member.MemberUserId == userId && member.Status == "accepted"
                                                 && activeHistory.Status == "active"
                                                 && (!activeHistory.EndedAt.HasValue || activeHistory.EndedAt > now)
                                             select member.PlanMemberId).AnyAsync(ct);
            if (hasActiveSharedPlan) continue;

            user.PlanId = defaultPlan.PlanId;
            user.UpdatedAt = now;
            await SyncChannelQuotaAsync(db, userId, defaultPlan.StorageLimit, now, ct,
                memberUserIds.Contains(userId) ? 0 : null);
            db.PlanHistories.Add(new PlanHistory
            {
                PlanHistoryId = Guid.NewGuid(),
                UserId = userId,
                OwnerUserId = userId,
                PlanId = defaultPlan.PlanId,
                Status = "active",
                StartedAt = now,
                EndedAt = now.AddDays(defaultPlan.DurationDays),
                AutoRenew = false,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<Plan?> FindDefaultPlanAsync(HuTubeDbContext db, CancellationToken ct) =>
        await db.Plans.AsNoTracking()
            .Where(plan => plan.Status == "active" && plan.IsDefaultForNewUsers)
            .OrderBy(plan => plan.DisplayOrder)
            .ThenBy(plan => plan.Price)
            .FirstOrDefaultAsync(ct)
        ?? await db.Plans.AsNoTracking()
            .Where(plan => plan.Status == "active" && plan.Code == "free")
            .FirstOrDefaultAsync(ct);

    private static async Task LockMembershipAsync(HuTubeDbContext db, Guid userId, CancellationToken ct)
    {
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"plan-membership:" + userId}, 0))", ct);
    }

    private static async Task SyncChannelQuotaAsync(
        HuTubeDbContext db, Guid userId, long storageLimit, DateTimeOffset now, CancellationToken ct, long? storageUsed = null)
    {
        var channelId = await db.Channels.AsNoTracking()
            .Where(channel => channel.OwnerUserId == userId && channel.Status == "active")
            .Select(channel => channel.ChannelId)
            .SingleOrDefaultAsync(ct);
        if (channelId == Guid.Empty) return;

        var quota = await db.ChannelQuotas.SingleOrDefaultAsync(item => item.ChannelId == channelId, ct);
        if (quota == null)
            db.ChannelQuotas.Add(new ChannelQuota { ChannelId = channelId, StorageLimit = storageLimit, StorageUsed = storageUsed ?? 0, UpdatedAt = now });
        else
        {
            quota.StorageLimit = storageLimit;
            if (storageUsed.HasValue) quota.StorageUsed = storageUsed.Value;
            quota.UpdatedAt = now;
        }
    }
}
