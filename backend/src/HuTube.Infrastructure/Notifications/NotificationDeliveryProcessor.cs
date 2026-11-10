using System.Text.Json;
using HuTube.Application.Auth;
using HuTube.Application.Notifications;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Notifications;

public interface IPushNotificationSender
{
    bool Enabled { get; }
    // false means the registration token is permanently invalid.
    Task<bool> SendAsync(string token, NotificationMessage message, Guid notificationId, CancellationToken ct);
}

public sealed class NotificationDeliveryProcessor(HuTubeDbContext db, IHubContext<NotificationHub> hub,
    IAuthEmailSender emails, IPushNotificationSender push, AuthOptions auth, TimeProvider clock,
    ILogger<NotificationDeliveryProcessor> logger)
{
    public Task<DateTimeOffset?> NextDueAsync(CancellationToken ct) => db.NotificationDeliveries.AsNoTracking()
        .Where(delivery => delivery.CompletedAt == null)
        .Select(delivery => (DateTimeOffset?)delivery.AvailableAt).MinAsync(ct);

    public async Task<bool> DeliverNextAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var delivery = await db.NotificationDeliveries.FromSqlInterpolated($"""
            SELECT * FROM notification_deliveries WHERE completed_at IS NULL AND available_at <= {now}
            ORDER BY available_at, delivery_id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
        if (delivery == null) return false;
        var message = JsonSerializer.Deserialize<NotificationMessage>(delivery.Payload)
            ?? throw new JsonException("Missing notification delivery payload.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.UserId == delivery.UserId && user.Status == "active", ct);
        var setting = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(setting => setting.UserId == delivery.UserId, ct);
        if (user == null || (message.Type == "new_video" ? setting?.NewVideoEnabled == false : setting?.ModerationEnabled == false))
            delivery.RealtimeDone = delivery.EmailDone = delivery.PushDone = true;
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var failures = new List<Exception>();
            if (!delivery.RealtimeDone)
            {
                try
                {
                    if (setting?.InAppEnabled != false && delivery.NotificationId.HasValue)
                    {
                        var row = await db.Notifications.AsNoTracking().SingleOrDefaultAsync(row => row.NotificationId == delivery.NotificationId, timeout.Token);
                        if (row != null)
                        {
                            var client = hub.Clients.User(delivery.UserId.ToString());
                            await client.SendAsync("NotificationReceived", new NotificationResponse(row.NotificationId, row.Type, row.Title,
                                row.Content, row.ActionUrl, row.IsRead, row.CreatedAt, row.ResourceType, row.ResourceId), timeout.Token);
                            await client.SendAsync("UnreadCountChanged", await db.Notifications.CountAsync(row => row.UserId == delivery.UserId && !row.IsRead, timeout.Token), timeout.Token);
                        }
                    }
                    delivery.RealtimeDone = true;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { failures.Add(ex); }
            }
            if (!delivery.EmailDone)
            {
                try
                {
                    if (setting?.EmailEnabled != false)
                    {
                        var url = ResolveActionUrl(message.ActionUrl, auth.WebBaseUrl);
                        await emails.SendTemplateAsync(user.Email, new(message.Title, "THÔNG BÁO TỪ DALITU", message.Title,
                            [message.Content], url == null ? null : "Mở trên Dalitu", url), timeout.Token);
                    }
                    delivery.EmailDone = true;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { failures.Add(ex); }
            }
            if (!delivery.PushDone)
            {
                try
                {
                    if (push.Enabled && setting?.InAppEnabled != false)
                    {
                        var devices = await (from device in db.PushDevices
                            join session in db.Sessions.AsNoTracking() on device.SessionId equals session.SessionId
                            where device.UserId == delivery.UserId && session.UserId == delivery.UserId
                                && session.RevokedAt == null && session.ExpiresAt > now
                            select device).ToListAsync(timeout.Token);
                        foreach (var device in devices)
                            if (!await push.SendAsync(device.Token, message, delivery.NotificationId ?? delivery.DeliveryId, timeout.Token))
                                db.PushDevices.Remove(device);
                    }
                    delivery.PushDone = true;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { failures.Add(ex); }
            }
            if (failures.Count > 0)
            {
                delivery.Attempts++;
                delivery.LastError = string.Join(",", failures.Select(error => error.GetType().Name).Distinct());
                delivery.AvailableAt = clock.GetUtcNow().AddSeconds(ModerationOutboxPublisher.RetrySeconds(delivery.Attempts));
                logger.LogWarning("Notification delivery {DeliveryId} incomplete; retry {Attempt}: {Errors}", delivery.DeliveryId, delivery.Attempts, delivery.LastError);
            }
        }
        if (delivery.RealtimeDone && delivery.EmailDone && delivery.PushDone)
        { delivery.CompletedAt = clock.GetUtcNow(); delivery.LastError = null; }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static string? ResolveActionUrl(string? action, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(action)) return null;
        if (Uri.TryCreate(action, UriKind.Absolute, out var absolute))
            return absolute.Scheme is "http" or "https" ? action : null;
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) && origin.Scheme is "http" or "https"
            ? new Uri(origin, action).ToString() : null;
    }
}
