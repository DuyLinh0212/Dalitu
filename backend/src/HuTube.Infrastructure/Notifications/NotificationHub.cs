using System.Security.Claims;
using HuTube.Application.Auth;
using HuTube.Application.Notifications;
using HuTube.Application.Videos;
using HuTube.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Notifications;

[Authorize]
public sealed class NotificationHub(NotificationConnections connections) : Hub
{
    public override Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirst("sid")?.Value, out var sessionId))
        { Context.Abort(); return Task.CompletedTask; }
        connections.Active[Context.ConnectionId] = (sessionId, Context);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Active.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class NotificationConnections
{
    internal ConcurrentDictionary<string, (Guid SessionId, HubCallerContext Context)> Active { get; } = new();
}

public sealed class NotificationSessionMonitor(IServiceScopeFactory scopes, NotificationConnections connections) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (connections.Active.IsEmpty) continue;
            var snapshot = connections.Active.ToArray();
            var ids = snapshot.Select(x => x.Value.SessionId).Distinct().ToArray();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
                var now = DateTimeOffset.UtcNow;
                var valid = (await db.Sessions.AsNoTracking().Where(x => ids.Contains(x.SessionId) && x.RevokedAt == null && x.ExpiresAt > now)
                    .Select(x => x.SessionId).ToListAsync(stoppingToken)).ToHashSet();
                foreach (var pair in snapshot)
                    if (!valid.Contains(pair.Value.SessionId)) pair.Value.Context.Abort();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch
            {
                foreach (var pair in snapshot) pair.Value.Context.Abort();
            }
        }
    }
}

public sealed class NotificationUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst("sub")?.Value ?? connection.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}

public sealed class NotificationService(
    HuTubeDbContext db,
    IHubContext<NotificationHub> hub,
    IAuthEmailSender emailSender,
    TimeProvider clock,
    AuthOptions? authOptions = null,
    HybridCache? cache = null,
    ILogger<NotificationService>? logger = null) : INotificationService
{
    private static string CacheKey(Guid userId) => $"notifications:{userId:N}:recent";

    public async Task<Func<CancellationToken, Task>> PrepareBatchAsync(IReadOnlyList<NotificationMessage> messages, CancellationToken ct = default)
    {
        var rows = new List<HuTube.Domain.Videos.Notification>();
        var emails = new List<(string Address, NotificationMessage Message)>();
        var unread = new Dictionary<Guid, int>();
        // Bound SQL parameters and avoid one settings/read/save/count round trip per subscriber.
        foreach (var userIds in messages.Select(x => x.UserId).Distinct().Order().Chunk(500))
        {
            var settings = await db.NotificationSettings.AsNoTracking().Where(x => userIds.Contains(x.UserId)).ToDictionaryAsync(x => x.UserId, ct);
            var batch = messages.Where(x => userIds.Contains(x.UserId)).ToArray();
            var emailIds = batch.Where(x => x.SendEmail).Select(x => x.UserId).Distinct().ToArray();
            var addresses = emailIds.Length == 0 ? new Dictionary<Guid, string>()
                : await db.Users.AsNoTracking().Where(x => emailIds.Contains(x.UserId)).ToDictionaryAsync(x => x.UserId, x => x.Email, ct);
            foreach (var message in batch)
            {
                settings.TryGetValue(message.UserId, out var setting);
                var enabled = message.Type switch
                {
                    "new_video" => setting?.NewVideoEnabled ?? true,
                    "moderation" => setting?.ModerationEnabled ?? true,
                    _ => true
                };
                if (!enabled) continue;
                if (setting?.InAppEnabled ?? true)
                    rows.Add(new HuTube.Domain.Videos.Notification {
                        UserId = message.UserId, Type = message.Type, Title = message.Title, Content = message.Content,
                        ActionUrl = message.ActionUrl, ResourceType = message.ResourceType, ResourceId = message.ResourceId,
                        CreatedAt = clock.GetUtcNow()
                    });
                if (message.SendEmail && (setting?.EmailEnabled ?? true) && addresses.TryGetValue(message.UserId, out var address))
                    emails.Add((address, message));
            }
        }
        db.Notifications.AddRange(rows);
        await db.SaveChangesAsync(ct);
        foreach (var userIds in rows.Select(x => x.UserId).Distinct().Chunk(500))
        {
            var counts = await db.Notifications.AsNoTracking().Where(x => userIds.Contains(x.UserId) && !x.IsRead)
                .GroupBy(x => x.UserId).Select(x => new { UserId = x.Key, Count = x.Count() }).ToListAsync(ct);
            foreach (var count in counts) unread[count.UserId] = count.Count;
        }
        // Notification cache keys use the transactional SQL revision. No per-recipient
        // Redis removal is needed, and no external service holds the moderation lock.
        return async token =>
        {
            await Parallel.ForEachAsync(rows.GroupBy(x => x.UserId), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (group, cancellation) =>
            {
                try
                {
                    var client = hub.Clients.User(group.Key.ToString());
                    foreach (var row in group) await client.SendAsync("NotificationReceived", ToResponse(row), cancellation);
                    await client.SendAsync("UnreadCountChanged", unread.GetValueOrDefault(group.Key), cancellation);
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested)
                { logger?.LogWarning(ex, "Realtime notification delivery failed after commit"); }
            });
            await Parallel.ForEachAsync(emails, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (entry, cancellation) =>
            {
                var message = entry.Message;
                var action = ResolveActionUrl(message.ActionUrl, authOptions?.WebBaseUrl);
                try
                {
                    await emailSender.SendTemplateAsync(entry.Address, new(message.Title, "THÔNG BÁO TỪ DALITU", message.Title,
                        [message.Content], action is null ? null : "Mở trên Dalitu", action), cancellation);
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested)
                { logger?.LogWarning(ex, "Notification email delivery failed after commit"); }
            });
        };
    }

    public async Task<NotificationPage> GetAsync(Guid userId, int page = 1, int pageSize = 5, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 25);
        if (cache != null && db.Database.IsNpgsql())
        {
            string revision;
            try { revision = await db.Database.SqlQuery<string>($"""
                SELECT COALESCE((SELECT revision::text FROM public.read_cache_revisions
                    WHERE scope = {"notifications:" + userId.ToString()}),
                    (SELECT revision::text FROM public.read_cache_revisions WHERE scope = 'shared')) AS "Value"
                """).SingleAsync(ct); }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UndefinedTable)
            { return await ReadPageAsync(userId, page, pageSize, ct); }
            return await cache.GetOrCreateAsync($"notifications:v1:{userId:N}:{revision}:{page}:{pageSize}",
                async token => await ReadPageAsync(userId, page, pageSize, token),
                new HybridCacheEntryOptions {
                    Expiration = TimeSpan.FromSeconds(30),
                    // Use only the distributed cache for this mutable user state.
                    Flags = HybridCacheEntryFlags.DisableLocalCache
                }, cancellationToken: ct);
        }
        return await ReadPageAsync(userId, page, pageSize, ct);
    }

    private async Task<NotificationPage> ReadPageAsync(Guid userId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Notifications.AsNoTracking().Where(x => x.UserId == userId);
        var total = await query.CountAsync(ct);
        var unread = await query.CountAsync(x => !x.IsRead, ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.NotificationId)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(ToResponse).ToArray(), page, pageSize, total, unread, page * pageSize < total);
    }

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        var changed = await db.Notifications.Where(x => x.UserId == userId && x.NotificationId == notificationId && !x.IsRead)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsRead, true).SetProperty(x => x.ReadAt, clock.GetUtcNow()), ct);
        if (changed == 0 && !await db.Notifications.AnyAsync(x => x.UserId == userId && x.NotificationId == notificationId, ct))
            throw new ContentException(404, "NOTIFICATION_NOT_FOUND", "Không tìm thấy thông báo.");
        if (cache != null) await cache.RemoveAsync(CacheKey(userId), CancellationToken.None);
        await hub.Clients.User(userId.ToString()).SendAsync("UnreadCountChanged", await UnreadAsync(userId, ct), ct);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        await db.Notifications.Where(x => x.UserId == userId && !x.IsRead)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsRead, true).SetProperty(x => x.ReadAt, clock.GetUtcNow()), ct);
        if (cache != null) await cache.RemoveAsync(CacheKey(userId), CancellationToken.None);
        await hub.Clients.User(userId.ToString()).SendAsync("UnreadCountChanged", 0, ct);
    }

    public Task PublishInAppAsync(Guid userId, string type, string title, string content, string? actionUrl,
        string? resourceType, Guid? resourceId, CancellationToken ct = default) =>
        PublishInternalAsync(userId, type, title, content, actionUrl, resourceType, resourceId, false, ct);

    public Task PublishAsync(Guid userId, string type, string title, string content, string? actionUrl,
        string? resourceType, Guid? resourceId, CancellationToken ct = default) =>
        PublishInternalAsync(userId, type, title, content, actionUrl, resourceType, resourceId, true, ct);

    public async Task PublishInvitationAsync(Guid userId, InvitationSignal invitation, CancellationToken ct = default)
    {
        await PublishInAppAsync(userId, "channel_invitation", $"Lời mời tham gia kênh {invitation.ChannelName}",
            $"Bạn được mời tham gia với vai trò {invitation.RoleCode}.", $"/channel-invitations?invitation={invitation.InvitationId}",
            "channel_invitation", invitation.InvitationId, ct);
        await hub.Clients.User(userId.ToString()).SendAsync("InvitationReceived", invitation, ct);
    }

    private async Task PublishInternalAsync(Guid userId, string type, string title, string content, string? actionUrl,
        string? resourceType, Guid? resourceId, bool sendEmail, CancellationToken ct)
    {
        var mandatoryInApp = type == "channel_invitation";
        var setting = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        var categoryEnabled = type switch
        {
            "comment_reply" or "video_comment" => setting?.CommentReplyEnabled ?? true,
            "mention" => setting?.MentionEnabled ?? true,
            "new_video" => setting?.NewVideoEnabled ?? true,
            "report_result" => setting?.ReportResultEnabled ?? true,
            "moderation" => setting?.ModerationEnabled ?? true,
            "plan_invitation" => setting?.PlanEnabled ?? true,
            "channel_activity" or "video_like" or "video_dislike" or "comment_like" => setting?.ChannelActivityEnabled ?? true,
            _ => true
        };
        if (!categoryEnabled) return;

        if (mandatoryInApp || (setting?.InAppEnabled ?? true))
        {
            var row = new HuTube.Domain.Videos.Notification { UserId = userId, Type = type, Title = title, Content = content,
                ActionUrl = actionUrl, ResourceType = resourceType, ResourceId = resourceId, CreatedAt = clock.GetUtcNow() };
            db.Notifications.Add(row); await db.SaveChangesAsync(ct);
            if (cache != null) await cache.RemoveAsync(CacheKey(userId), CancellationToken.None);
            await hub.Clients.User(userId.ToString()).SendAsync("NotificationReceived", ToResponse(row), ct);
            await hub.Clients.User(userId.ToString()).SendAsync("UnreadCountChanged", await UnreadAsync(userId, ct), ct);
        }
        if (sendEmail && (setting?.EmailEnabled ?? true))
        {
            var address = await db.Users.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.Email).SingleAsync(ct);
            var emailActionUrl = ResolveActionUrl(actionUrl, authOptions?.WebBaseUrl);
            await emailSender.SendTemplateAsync(address, new(
                title,
                "THÔNG BÁO TỪ DALITU",
                title,
                [content],
                emailActionUrl is null ? null : "Mở trên Dalitu",
                emailActionUrl), ct);
        }
    }

    private Task<int> UnreadAsync(Guid userId, CancellationToken ct) => db.Notifications.CountAsync(x => x.UserId == userId && !x.IsRead, ct);
    private static string? ResolveActionUrl(string? actionUrl, string? webBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(actionUrl)) return null;
        if (Uri.TryCreate(actionUrl, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https") return actionUrl;
        if (string.IsNullOrWhiteSpace(webBaseUrl)) return null;
        return $"{webBaseUrl.TrimEnd('/')}/{actionUrl.TrimStart('/')}";
    }

    private static NotificationResponse ToResponse(HuTube.Domain.Videos.Notification row) => new(row.NotificationId, row.Type,
        row.Title, row.Content, row.ActionUrl, row.IsRead, row.CreatedAt, row.ResourceType, row.ResourceId);
}
