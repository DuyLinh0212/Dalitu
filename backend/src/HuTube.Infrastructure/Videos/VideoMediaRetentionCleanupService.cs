using HuTube.Application.Storage;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Videos;

/// <summary>
/// Removes the source and rendition objects after the appeal window has ended.
/// Pending appeals hold the media until a reviewer makes a final decision.
/// </summary>
public sealed class VideoMediaRetentionCleanupService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<VideoMediaRetentionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunCleanupSafelyAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(BackgroundWorkSchedule.UntilNextBoundary(Interval, clock.GetUtcNow()), clock, stoppingToken);
                await RunCleanupSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunCleanupSafelyAsync(CancellationToken ct)
    {
        try
        {
            await CleanupBatchAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Không thể dọn media video đã hết thời hạn khiếu nại.");
        }
    }

    private async Task CleanupBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var now = clock.GetUtcNow();

        var videos = await db.Videos
            .Where(video => (video.Status == "deleted" || (video.Status == "blocked" && video.ModerationStatus == "rejected"))
                && video.MediaRetentionUntil.HasValue
                && video.MediaRetentionUntil <= now
                && video.MediaPurgedAt == null
                && !db.Appeals.Any(appeal => appeal.TargetType == AppealTargetTypes.Video
                    && appeal.TargetId == video.VideoId
                    && (appeal.Status == AppealStatuses.Pending
                        || appeal.Status == AppealStatuses.Reviewing
                        || appeal.Status == "escalated")))
            .OrderBy(video => video.MediaRetentionUntil)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (videos.Count == 0) return;

        var videoIds = videos.Select(video => video.VideoId).ToArray();
        var renditions = await db.VideoRenditions
            .Where(rendition => videoIds.Contains(rendition.VideoId))
            .ToListAsync(ct);

        foreach (var video in videos)
        {
            if (db.Entry(video).State == EntityState.Detached) db.Attach(video);
            var videoRenditions = renditions.Where(rendition => rendition.VideoId == video.VideoId).ToArray();
            foreach (var rendition in videoRenditions)
                if (db.Entry(rendition).State == EntityState.Detached) db.Attach(rendition);
            var paths = videoRenditions.Select(rendition => rendition.FileUrl)
                .Append(video.VideoUrl)
                .Append(video.ThumbnailUrl)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            try
            {
                // Persist intent before external deletion. Keep object keys for retry
                // and keep the existing non-empty URL constraints valid.
                await using (var claim = await db.Database.BeginTransactionAsync(ct))
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"purge:" + video.VideoId}, 0))", ct);
                    await db.Entry(video).ReloadAsync(ct);
                    if (video.MediaPurgedAt.HasValue || !(video.Status == "deleted" || (video.Status == "blocked" && video.ModerationStatus == "rejected"))
                        || video.MediaRetentionUntil == null || video.MediaRetentionUntil > now
                        || await db.Appeals.AnyAsync(appeal => appeal.TargetType == AppealTargetTypes.Video && appeal.TargetId == video.VideoId
                            && (appeal.Status == AppealStatuses.Pending || appeal.Status == AppealStatuses.Reviewing || appeal.Status == "escalated"), ct)) continue;
                    video.MediaPurgeStartedAt ??= now;
                    await db.SaveChangesAsync(ct);
                    await claim.CommitAsync(ct);
                }
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"purge:" + video.VideoId}, 0))", ct);
                await db.Entry(video).ReloadAsync(ct);
                if (video.MediaPurgedAt.HasValue) continue;
                foreach (var path in paths)
                    await storage.DeleteFileAsync(path!, ct);

                video.MediaPurgedAt = now;
                video.ThumbnailUrl = null;
                video.UpdatedAt = now;
                foreach (var rendition in videoRenditions)
                {
                    rendition.Status = "deleted";
                    rendition.UpdatedAt = now;
                }
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"quota:" + video.ChannelId}, 0))", ct);
                await db.SaveChangesAsync(ct);
                var activeStorage = await db.Videos.AsNoTracking()
                    .Where(item => item.ChannelId == video.ChannelId && item.Status != "deleted" && item.MediaPurgedAt == null)
                    .SumAsync(item => (long?)item.FileSize, ct) ?? 0L;
                await db.ChannelQuotas.Where(x => x.ChannelId == video.ChannelId)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.StorageUsed, activeStorage)
                        .SetProperty(x => x.UpdatedAt, now), ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                logger.LogInformation("Đã dọn media video {VideoId} sau thời hạn khiếu nại.", video.VideoId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(ex, "Không dọn được toàn bộ media video {VideoId}; lần chạy sau sẽ thử lại.", video.VideoId);
            }
        }

    }
}
