using System.Text.Json;
using HuTube.Application.Notifications;
using HuTube.Application.Serialization;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HuTube.Infrastructure.Notifications;

public sealed class NotificationFanout(HuTubeDbContext db, TimeProvider clock,
    BackgroundWorkWakeup<NotificationDeliveryWorker>? wakeup = null)
{
    // A cursor and all rows for one bounded batch commit together. Redeliveries
    // resume this cursor and cannot create duplicate inbox notifications.
    public async Task<bool> ProcessBatchAsync(VideoModerationEvent message, int batchSize = 250, CancellationToken ct = default)
    {
        if (message.EventId == Guid.Empty || message.OwnerNotifications == null)
            throw new JsonException("Invalid moderation event.");
        batchSize = Math.Clamp(batchSize, 1, 1000);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO notification_inbox_events (event_id, owner_processed) VALUES ({message.EventId}, false) ON CONFLICT DO NOTHING", ct);
        var inbox = await db.NotificationInboxEvents.FromSqlInterpolated($"SELECT * FROM notification_inbox_events WHERE event_id = {message.EventId} FOR UPDATE").SingleAsync(ct);
        if (inbox.CompletedAt != null) { await transaction.CommitAsync(ct); return true; }
        var messages = new List<NotificationMessage>();
        if (!inbox.OwnerProcessed)
        {
            messages.AddRange(message.OwnerNotifications);
            inbox.OwnerProcessed = true;
        }

        // Do not announce private, removed or subsequently blocked videos.
        var canAnnounce = message.NotifySubscribers && await (
            from video in db.Videos.AsNoTracking()
            join channel in db.Channels.AsNoTracking() on video.ChannelId equals channel.ChannelId
            where video.VideoId == message.VideoId && video.ChannelId == message.ChannelId
                && video.Status == "published" && video.ModerationStatus == "approved" && video.Visibility == "public"
                && channel.Status == "active"
            select video.VideoId).AnyAsync(ct);
        var subscriptions = canAnnounce
            ? await db.Subscriptions.FromSqlInterpolated($"""
                SELECT * FROM subscriptions WHERE channel_id = {message.ChannelId}
                    AND status = 'active' AND notifications_enabled AND subscribed_at <= {message.OccurredAt}
                    AND subscription_id > {inbox.LastSubscriptionId ?? Guid.Empty}
                ORDER BY subscription_id LIMIT {batchSize}
                """).AsNoTracking().ToListAsync(ct) : [];
        messages.AddRange(subscriptions.Select(subscription => new NotificationMessage(subscription.UserId, "new_video",
            $"{message.ChannelName} vừa đăng video mới", message.VideoTitle, $"/watch/{message.VideoId}", "video", message.VideoId, SendEmail: false)));
        if (subscriptions.Count > 0) inbox.LastSubscriptionId = subscriptions[^1].SubscriptionId;
        if (subscriptions.Count < batchSize) inbox.CompletedAt = clock.GetUtcNow();

        var userIds = messages.Select(item => item.UserId).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(user => userIds.Contains(user.UserId) && user.Status == "active")
            .Select(user => user.UserId).ToListAsync(ct);
        var settings = await db.NotificationSettings.AsNoTracking().Where(item => userIds.Contains(item.UserId))
            .ToDictionaryAsync(item => item.UserId, ct);
        var existing = await db.NotificationDeliveries.AsNoTracking().Where(item => item.EventId == message.EventId && userIds.Contains(item.UserId))
            .Select(item => new { item.UserId, item.Type }).ToListAsync(ct);
        foreach (var item in messages.DistinctBy(item => (item.UserId, item.Type)))
        {
            if (!users.Contains(item.UserId) || existing.Any(row => row.UserId == item.UserId && row.Type == item.Type)) continue;
            settings.TryGetValue(item.UserId, out var preference);
            if (item.Type == "new_video" ? preference?.NewVideoEnabled == false : preference?.ModerationEnabled == false) continue;
            Notification? notification = null;
            if (preference?.InAppEnabled != false)
            {
                notification = new Notification { UserId = item.UserId, Type = item.Type, Title = item.Title,
                    Content = item.Content, ActionUrl = item.ActionUrl, ResourceType = item.ResourceType,
                    ResourceId = item.ResourceId, CreatedAt = message.OccurredAt };
                db.Notifications.Add(notification);
            }
            db.NotificationDeliveries.Add(new NotificationDelivery {
                DeliveryId = Guid.NewGuid(), EventId = message.EventId, UserId = item.UserId, Type = item.Type,
                NotificationId = notification?.NotificationId, Payload = PersistenceJson.Serialize(item),
                RealtimeDone = notification == null, PushDone = preference?.InAppEnabled == false,
                EmailDone = !item.SendEmail || preference?.EmailEnabled == false, AvailableAt = clock.GetUtcNow()
            });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        wakeup?.Signal();
        return inbox.CompletedAt != null;
    }
}
