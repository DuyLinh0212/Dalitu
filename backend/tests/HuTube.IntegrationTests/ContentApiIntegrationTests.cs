using HuTube.Application.Payments;
using HuTube.Application.Plans;
using HuTube.Infrastructure.Payments;
using HuTube.Infrastructure.Persistence;
using HuTube.Infrastructure.Videos;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HuTube.Application.Playlists;
using HuTube.Application.Account;
using HuTube.Application.Auth;
using HuTube.Application.Channels;
using HuTube.Application.Videos;
using HuTube.Application.Notifications;
using HuTube.Domain.Videos;
using HuTube.Domain.Plans;
using HuTube.Domain.Channels;
using HuTube.Api.Controllers;
using Microsoft.EntityFrameworkCore;

namespace HuTube.IntegrationTests;

public sealed class ContentApiIntegrationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task HourlyAnalytics_GroupsEventsAndEnforcesChannelPermissions()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("hourly_owner");
        var (viewer, viewerId, _) = await CreateUserAndChannelAsync("hourly_viewer");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Hourly test");
        var second = await CreatePublishedVideoAsync(owner, channelId, "Other hourly test");
        (await viewer.PostAsync($"/api/v1/channels/{channelId}/subscribe", null)).EnsureSuccessStatusCode();
        (await viewer.PostAsync($"/api/v1/channels/{channelId}/subscribe", null)).EnsureSuccessStatusCode();
        (await viewer.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/reaction", new { type = "like" })).EnsureSuccessStatusCode();
        (await viewer.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/reaction", new { type = "like" })).EnsureSuccessStatusCode();
        (await viewer.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/watch-progress", new { watchedSeconds = 10, saveHistory = true })).EnsureSuccessStatusCode();
        var sampleTime = DateTimeOffset.UtcNow.AddHours(-2);
        await using (var db = factory.CreateDb())
        {
            db.ChannelAnalyticsEvents.Add(new ChannelAnalyticsEvent { ChannelId = channelId, VideoId = video.VideoId,
                OccurredAt = sampleTime, Metric = "watchSeconds", Value = 40 });
            db.ChannelAnalyticsEvents.Add(new ChannelAnalyticsEvent { ChannelId = channelId, VideoId = second.VideoId,
                OccurredAt = sampleTime, Metric = "likes", Value = 7 });
            await db.SaveChangesAsync();
        }
        var url = $"/api/v1/studio/channels/{channelId}/hourly-analytics?days=7&utcOffsetMinutes=420&videoId={video.VideoId}";
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(url)).StatusCode);
        var result = await owner.GetFromJsonAsync<StudioHourlyAnalytics>(url, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(24, result!.Hours.Count);
        Assert.Equal(1, result.Hours.Sum(h => h.Views));
        Assert.Equal(1, result.Hours.Sum(h => h.Likes));
        Assert.Equal(1, result.Hours.Sum(h => h.Subscribers));
        Assert.Equal(40, result.Hours.Single(h => h.Hour == sampleTime.AddMinutes(420).Hour).WatchSeconds);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(url.Replace("days=7", "days=500"))).StatusCode);

        await using (var db = factory.CreateDb())
        {
            db.ChannelMembers.Add(new ChannelMember { ChannelId = channelId, UserId = viewerId, RoleCode = ChannelRoles.Editor, Status = "active" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchivePlan_StopsNewPurchasesAndKeepsExistingBenefitsUntilExpiry(bool sharedMember)
    {
        // Arrange
        var (owner, ownerId, ownerChannelId) = await CreateUserAndChannelAsync("archive_owner");
        var (member, memberId, memberChannelId) = await CreateUserAndChannelAsync("archive_member");
        var (outsider, outsiderId, _) = await CreateUserAndChannelAsync("archive_outsider");
        var (_, adminId, _) = await CreateUserAndChannelAsync("archive_admin");
        string adminEmail;
        string outsiderEmail;
        await using (var db = factory.CreateDb())
        {
            var adminUser = await db.Users.SingleAsync(u => u.UserId == adminId);
            adminUser.RoleId = HuTube.Domain.Rbac.SystemRoles.SuperAdmin;
            adminEmail = adminUser.Email;
            outsiderEmail = await db.Users.Where(u => u.UserId == outsiderId).Select(u => u.Email).SingleAsync();
            await db.SaveChangesAsync();
        }
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-HuTube-Client", "web");
        admin.DefaultRequestHeaders.Add("X-HuTube-App", "admin");
        var login = await admin.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(adminEmail, "Password123!", "admin", "Archive test"));
        login.EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions))!.AccessToken);
        var actor = sharedMember ? member : owner;
        var actorChannelId = sharedMember ? memberChannelId : ownerChannelId;
        var video = await CreatePublishedVideoAsync(actor, actorChannelId, "Archived subscription benefits");
        const long mb = 1024 * 1024;
        var created = await admin.PostAsJsonAsync("/api/v1/admin/plans", new CreatePlanRequest(
            $"archive-{Guid.NewGuid():N}", "Archive fixture", null, 100000, 30, 100 * mb, 10 * mb, 600,
            "1080p", 4, "{\"download\":true,\"pip\":true,\"background_play\":true,\"video_promotion\":true}", MaxDownloadQuality: "1080p"));
        created.EnsureSuccessStatusCode();
        var paid = (await created.Content.ReadFromJsonAsync<PlanResponse>(JsonOptions))!;
        var expires = DateTimeOffset.UtcNow.AddDays(2);
        Guid historyId;
        await using (var db = factory.CreateDb())
        {
            await db.PlanHistories.Where(h => h.UserId == ownerId && h.Status == "active")
                .ExecuteUpdateAsync(update => update.SetProperty(h => h.Status, "expired"));
            (await db.Users.SingleAsync(u => u.UserId == ownerId)).PlanId = paid.PlanId;
            var history = new PlanHistory { UserId = ownerId, OwnerUserId = ownerId, PlanId = paid.PlanId,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), EndedAt = expires, OwnerAllocatedStorage = 40 * mb };
            historyId = history.PlanHistoryId;
            db.PlanHistories.Add(history);
            if (sharedMember)
            {
                var user = await db.Users.SingleAsync(u => u.UserId == memberId);
                user.PlanId = paid.PlanId;
                db.PlanMembers.Add(new PlanMember { PlanHistoryId = historyId, OwnerUserId = ownerId,
                    MemberUserId = memberId, MemberEmail = user.Email, Status = "accepted",
                    AcceptedAt = DateTimeOffset.UtcNow, AllocatedStorage = 50 * mb });
            }
            await db.ChannelQuotas.Where(q => q.ChannelId == ownerChannelId)
                .ExecuteUpdateAsync(update => update.SetProperty(q => q.StorageLimit, 40 * mb));
            if (sharedMember) await db.ChannelQuotas.Where(q => q.ChannelId == memberChannelId)
                .ExecuteUpdateAsync(update => update.SetProperty(q => q.StorageLimit, 50 * mb));
            await db.SaveChangesAsync();
        }
        var before = (await actor.GetFromJsonAsync<PlanDetailResponse>("/api/v1/plans/my-plan", JsonOptions))!;

        // Act
        var forbiddenArchive = await outsider.PostAsync($"/api/v1/admin/plans/{paid.PlanId}/archive", null);
        var archived = await admin.PostAsync($"/api/v1/admin/plans/{paid.PlanId}/archive", null);
        var current = (await actor.GetFromJsonAsync<PlanDetailResponse>("/api/v1/plans/my-plan", JsonOptions))!;
        var preflight = await actor.PostAsJsonAsync("/api/v1/videos/upload-preflight",
            new { channelId = actorChannelId, fileSize = 1024, duration = 60, contentType = "video/mp4", sourceQuality = "1080p" });
        using var download = await actor.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/download-file", new { quality = "720p" });
        var promotion = await actor.PatchAsJsonAsync($"/api/v1/videos/{video.VideoId}", new { promotionEnabled = true });

        // Assert: archive is a catalogue change, not cancellation of the purchased term.
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenArchive.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        Assert.Equal(paid.PlanId, current.PlanId);
        Assert.Equal("archived", current.Status);
        Assert.Equal(sharedMember, current.IsSharedMember);
        Assert.Equal(before.Subscription!.EndedAt, current.Subscription!.EndedAt);
        Assert.Equal(before.StorageLimit, current.StorageLimit);
        Assert.Equal(before.MaxUploadSize, current.MaxUploadSize);
        Assert.Equal("1080p", current.MaxVideoQuality);
        Assert.Equal("1080p", current.MaxDownloadQuality);
        foreach (var feature in new[] { "pip", "background_play", "download", "video_promotion" }) Assert.True(current.Features![feature]);
        preflight.EnsureSuccessStatusCode();
        download.EnsureSuccessStatusCode();
        promotion.EnsureSuccessStatusCode();
        Assert.True((await promotion.Content.ReadFromJsonAsync<VideoResponse>(JsonOptions))!.PromotionEnabled);
        var home = await factory.CreateClient().GetFromJsonAsync<PageResult<VideoCardResponse>>("/api/v1/feed/home?pageSize=100", JsonOptions);
        Assert.Contains(home!.Items, item => item.VideoId == video.VideoId && item.IsPromoted);
        Assert.DoesNotContain((await factory.CreateClient().GetFromJsonAsync<List<PlanResponse>>("/api/v1/plans", JsonOptions))!, p => p.PlanId == paid.PlanId);
        Assert.Contains((await admin.GetFromJsonAsync<List<PlanResponse>>("/api/v1/admin/plans", JsonOptions))!, p => p.PlanId == paid.PlanId && p.Status == "archived");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/v1/plans/{paid.PlanId}/subscribe", new { autoRenew = false })).StatusCode);
        await using (var paymentDb = factory.CreateDb())
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var payments = new PaymentService(paymentDb, scope.ServiceProvider.GetRequiredService<IPlanService>(), PaymentOptions(), TimeProvider.System);
            var rejectedPurchase = await Assert.ThrowsAsync<PaymentException>(() => payments.InitiateAsync(outsiderId, new CreatePaymentRequest(paid.PlanId)));
            Assert.Equal(404, rejectedPurchase.Status);
            Assert.Equal("PLAN_NOT_FOUND", rejectedPurchase.Code);
        }
        await using (var db = factory.CreateDb())
        {
            var history = await db.PlanHistories.SingleAsync(h => h.PlanHistoryId == historyId);
            Assert.Equal("active", history.Status);
            Assert.Equal(expires.ToUnixTimeSeconds(), history.EndedAt!.Value.ToUnixTimeSeconds());
            Assert.False(await db.Payments.AnyAsync(p => p.UserId == outsiderId && p.PlanId == paid.PlanId));
        }
        if (sharedMember)
        {
            // Inviting a member is a benefit of the existing purchased term.
            var invited = await owner.PostAsJsonAsync("/api/v1/plans/members/invite", new { email = outsiderEmail, allocatedStorage = 10 * mb });
            invited.EnsureSuccessStatusCode();
            var invitation = (await invited.Content.ReadFromJsonAsync<PlanMemberResponse>(JsonOptions))!;
            string token;
            await using (var db = factory.CreateDb()) token = await db.PlanInvitationTokens.Where(t => t.PlanMemberId == invitation.PlanMemberId).Select(t => t.Token).SingleAsync();
            (await outsider.PostAsJsonAsync($"/api/v1/plans/members/{invitation.PlanMemberId}/accept", new { token })).EnsureSuccessStatusCode();
            Assert.Equal(paid.PlanId, (await outsider.GetFromJsonAsync<PlanDetailResponse>("/api/v1/plans/my-plan", JsonOptions))!.PlanId);
        }

        // At expiry, reads and protected operations must stop granting rights even before the worker runs.
        await using (var db = factory.CreateDb()) await db.PlanHistories.Where(h => h.PlanHistoryId == historyId)
            .ExecuteUpdateAsync(update => update.SetProperty(h => h.EndedAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var expired = (await actor.GetFromJsonAsync<PlanDetailResponse>("/api/v1/plans/my-plan", JsonOptions))!;
        Assert.Equal("free", expired.Code);
        Assert.False(expired.Features!["download"]);
        Assert.False(expired.Features["pip"]);
        Assert.False(expired.Features["background_play"]);
        // A pre-existing Free history may still be active on older shared accounts.
        Assert.NotEqual<Guid?>(historyId, expired.Subscription?.PlanHistoryId);
        Assert.False(expired.IsSharedMember);
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.GetAsync($"/api/v1/videos/{video.VideoId}/download-options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.PostAsJsonAsync("/api/v1/videos/upload-preflight",
            new { channelId = actorChannelId, fileSize = 1024, duration = 60, contentType = "video/mp4", sourceQuality = "1080p" })).StatusCode);
        var afterExpiryHome = await factory.CreateClient().GetFromJsonAsync<PageResult<VideoCardResponse>>("/api/v1/feed/home?pageSize=100", JsonOptions);
        Assert.DoesNotContain(afterExpiryHome!.Items, item => item.VideoId == video.VideoId && item.IsPromoted);
    }

    [Fact]
    public async Task CommentCreatorState_PersistsAndRequiresChannelPermission()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("creator_state_owner");
        var (viewer, _, _) = await CreateUserAndChannelAsync("creator_state_viewer");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Creator comment state");
        async Task<CommentResponse> Create(string text, Guid? parent = null)
        {
            var response = await viewer.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = text, parentCommentId = parent });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions))!;
        }
        var first = await Create("First comment");
        var second = await Create("Second comment");
        var reply = await Create("Reply", first.CommentId);

        var denied = await viewer.PutAsJsonAsync($"/api/v1/comments/{first.CommentId}/pin", new { enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedHeart = await viewer.PutAsJsonAsync($"/api/v1/comments/{first.CommentId}/creator-heart", new { enabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, deniedHeart.StatusCode);
        var replyPin = await owner.PutAsJsonAsync($"/api/v1/comments/{reply.CommentId}/pin", new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, replyPin.StatusCode);

        (await owner.PutAsJsonAsync($"/api/v1/comments/{first.CommentId}/pin", new { enabled = true })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/v1/comments/{first.CommentId}/creator-heart", new { enabled = true })).EnsureSuccessStatusCode();
        var read = await viewer.GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/videos/{video.VideoId}/comments?sort=newest", JsonOptions);
        Assert.Equal(first.CommentId, read!.Items[0].CommentId);
        Assert.True(read.Items[0].IsPinned);
        Assert.True(read.Items[0].HasCreatorHeart);

        (await owner.PutAsJsonAsync($"/api/v1/comments/{second.CommentId}/pin", new { enabled = true })).EnsureSuccessStatusCode();
        read = await viewer.GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/videos/{video.VideoId}/comments?sort=top", JsonOptions);
        Assert.Equal(second.CommentId, read!.Items[0].CommentId);
        Assert.Single(read.Items, item => item.IsPinned);
        Assert.True(read.Items.Single(item => item.CommentId == first.CommentId).HasCreatorHeart);

        (await owner.PutAsJsonAsync($"/api/v1/comments/{first.CommentId}/creator-heart", new { enabled = false })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/v1/comments/{second.CommentId}/pin", new { enabled = false })).EnsureSuccessStatusCode();
        read = await viewer.GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/videos/{video.VideoId}/comments", JsonOptions);
        Assert.DoesNotContain(read!.Items, item => item.IsPinned || item.HasCreatorHeart);

        var pins = await Task.WhenAll(
            owner.PutAsJsonAsync($"/api/v1/comments/{first.CommentId}/pin", new { enabled = true }),
            owner.PutAsJsonAsync($"/api/v1/comments/{second.CommentId}/pin", new { enabled = true }));
        foreach (var response in pins) response.EnsureSuccessStatusCode();
        read = await viewer.GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/videos/{video.VideoId}/comments", JsonOptions);
        var pinned = Assert.Single(read!.Items, item => item.IsPinned);
        (await owner.PatchAsJsonAsync($"/api/v1/comments/{pinned.CommentId}/visibility", new { hidden = true })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/v1/comments/{pinned.CommentId}/visibility", new { hidden = false })).EnsureSuccessStatusCode();
        read = await viewer.GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/videos/{video.VideoId}/comments", JsonOptions);
        Assert.DoesNotContain(read!.Items, item => item.IsPinned);
    }

    [Fact]
    public async Task NotificationPreferencesAndPaging_ControlReplyDelivery()
    {
        // Arrange
        var (owner, ownerId, channelId) = await CreateUserAndChannelAsync("notify_owner");
        var (viewer, _, _) = await CreateUserAndChannelAsync("notify_viewer");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Video notifications");
        var parentResponse = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Hỏi đáp", parentCommentId = (Guid?)null });
        var parent = await parentResponse.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions);
        await owner.PutAsJsonAsync("/api/v1/account/notifications", new
        {
            inAppEnabled = true, emailEnabled = false, newVideoEnabled = true, commentReplyEnabled = false,
            reportResultEnabled = true, moderationEnabled = true, planEnabled = true, recommendationEnabled = true,
            mentionEnabled = true, channelActivityEnabled = true, paymentEnabled = true
        });

        // Act
        await viewer.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Reply bị tắt", parentCommentId = parent!.CommentId });
        var disabled = await owner.GetFromJsonAsync<NotificationPage>("/api/v1/notifications", JsonOptions);
        await owner.PutAsJsonAsync("/api/v1/account/notifications", new
        {
            inAppEnabled = true, emailEnabled = false, newVideoEnabled = true, commentReplyEnabled = true,
            reportResultEnabled = true, moderationEnabled = true, planEnabled = true, recommendationEnabled = true,
            mentionEnabled = true, channelActivityEnabled = true, paymentEnabled = true
        });
        await viewer.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Reply được bật", parentCommentId = parent.CommentId });
        await using (var db = factory.CreateDb())
        {
            for (var i = 0; i < 6; i++) db.Notifications.Add(new Notification { UserId = ownerId, Type = "test", Title = $"Test {i}", Content = "Paging", CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i) });
            await db.SaveChangesAsync();
        }
        var firstPage = await owner.GetFromJsonAsync<NotificationPage>("/api/v1/notifications", JsonOptions);
        var missing = await owner.PatchAsJsonAsync($"/api/v1/notifications/{Guid.NewGuid()}/read", new { });

        // Assert
        Assert.DoesNotContain(disabled!.Items, x => x.Type == "comment_reply");
        Assert.Equal(5, firstPage!.Items.Count);
        Assert.True(firstPage.HasMore);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains(await factory.CreateDb().Notifications.AsNoTracking().ToListAsync(), x => x.UserId == ownerId && x.Type == "comment_reply");
    }

    [Fact]
    public async Task NotificationReadAllAndFeedSurfacesRemainScopedAndQueryable()
    {
        var (owner, ownerId, channelId) = await CreateUserAndChannelAsync("feed_surface_owner");
        var (viewer, viewerId, _) = await CreateUserAndChannelAsync("feed_surface_viewer");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Feed surface video");

        var subscribed = await viewer.PostAsync($"/api/v1/channels/{channelId}/subscribe", null);
        Assert.Equal(HttpStatusCode.OK, subscribed.StatusCode);

        await using (var db = factory.CreateDb())
        {
            db.Notifications.AddRange(
                new Notification { UserId = viewerId, Type = "test", Title = "Unread 1", Content = "one", CreatedAt = DateTimeOffset.UtcNow },
                new Notification { UserId = viewerId, Type = "test", Title = "Unread 2", Content = "two", CreatedAt = DateTimeOffset.UtcNow },
                new Notification { UserId = viewerId, Type = "test", Title = "Already read", Content = "read", IsRead = true, ReadAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow },
                new Notification { UserId = ownerId, Type = "test", Title = "Other user", Content = "untouched", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var unreadPage = await viewer.GetFromJsonAsync<NotificationPage>("/api/v1/notifications", JsonOptions);
        Assert.True(unreadPage!.UnreadCount >= 2);
        var readAll = await viewer.PostAsync("/api/v1/notifications/read-all", null);
        var readPage = await viewer.GetFromJsonAsync<NotificationPage>("/api/v1/notifications", JsonOptions);
        Assert.Equal(0, readPage!.UnreadCount);
        Assert.All(readPage.Items, item => Assert.True(item.IsRead));
        var guestExplore = await factory.CreateClient().GetFromJsonAsync<ExploreHubResponse>("/api/v1/feed/explore-hub", JsonOptions);
        var subscriptions = await viewer.GetFromJsonAsync<PageResult<VideoCardResponse>>(
            "/api/v1/feed/subscriptions?page=1&pageSize=20", JsonOptions);
        var subscribedChannels = await viewer.GetFromJsonAsync<List<SubscribedChannelResponse>>(
            "/api/v1/subscriptions", JsonOptions);
        var guestSubscriptions = await factory.CreateClient().GetAsync("/api/v1/feed/subscriptions");

        Assert.Equal(HttpStatusCode.NoContent, readAll.StatusCode);
        Assert.Contains(guestExplore!.Trending.Concat(guestExplore.TopVideos), item => item.VideoId == video.VideoId);
        Assert.Contains(subscriptions!.Items, item => item.VideoId == video.VideoId && item.ChannelId == channelId);
        Assert.Contains(subscribedChannels!, item => item.ChannelId == channelId);
        Assert.Equal(HttpStatusCode.Unauthorized, guestSubscriptions.StatusCode);

        await using var verify = factory.CreateDb();
        var viewerNotifications = await verify.Notifications.AsNoTracking().Where(x => x.UserId == viewerId).ToListAsync();
        Assert.All(viewerNotifications, item => Assert.True(item.IsRead && item.ReadAt.HasValue));
        Assert.Contains(await verify.Notifications.AsNoTracking().ToListAsync(), item => item.Title == "Other user" && !item.IsRead);
    }

    [Fact]
    public async Task NotificationRead_IsIdempotentAndRejectsForeignOwner()
    {
        // Arrange
        var (owner, ownerId, _) = await CreateUserAndChannelAsync("notification_read_owner");
        var (other, otherId, _) = await CreateUserAndChannelAsync("notification_read_other");
        var ownerNotificationId = Guid.NewGuid();
        var otherNotificationId = Guid.NewGuid();
        await using (var db = factory.CreateDb())
        {
            db.Notifications.AddRange(
                new HuTube.Domain.Videos.Notification
                {
                    NotificationId = ownerNotificationId,
                    UserId = ownerId,
                    Type = "payment",
                    Title = "Payment completed",
                    Content = "Gói đã được kích hoạt.",
                    ActionUrl = "/settings/billing",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new HuTube.Domain.Videos.Notification
                {
                    NotificationId = otherNotificationId,
                    UserId = otherId,
                    Type = "test",
                    Title = "Other user notification",
                    Content = "Không được truy cập.",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        // Warm the per-user cache before mutating the notification.
        var beforeRead = await owner.GetFromJsonAsync<NotificationPage>("/api/v1/notifications", JsonOptions);
        Assert.Equal(1, beforeRead!.UnreadCount);
        var otherPage = await other.GetFromJsonAsync<NotificationPage>("/api/v1/notifications", JsonOptions);
        Assert.DoesNotContain(otherPage!.Items, item => item.NotificationId == ownerNotificationId);

        // Act
        var firstRead = await owner.PatchAsJsonAsync($"/api/v1/notifications/{ownerNotificationId}/read", new { });
        var repeatedRead = await owner.PatchAsJsonAsync($"/api/v1/notifications/{ownerNotificationId}/read", new { });
        var foreignRead = await other.PatchAsJsonAsync($"/api/v1/notifications/{ownerNotificationId}/read", new { });
        var page = await owner.GetFromJsonAsync<NotificationPage>("/api/v1/notifications", JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, firstRead.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeatedRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.NotNull(page);
        Assert.Equal(0, page!.UnreadCount);
        Assert.Contains(page.Items, item => item.NotificationId == ownerNotificationId && item.IsRead);

        await using var verify = factory.CreateDb();
        var ownerNotification = await verify.Notifications.AsNoTracking()
            .SingleAsync(item => item.NotificationId == ownerNotificationId);
        var otherNotification = await verify.Notifications.AsNoTracking()
            .SingleAsync(item => item.NotificationId == otherNotificationId);
        Assert.True(ownerNotification.IsRead);
        Assert.NotNull(ownerNotification.ReadAt);
        Assert.False(otherNotification.IsRead);
    }

    [Fact]
    public async Task UploadModeratePublish_ExposesVideoInPublicFeedsAndPlayback()
    {
        // Arrange
        var (owner, ownerId, channelId) = await CreateUserAndChannelAsync("publisher");
        using var watermarkForm = new MultipartFormDataContent();
        var watermarkFile = new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j8ioAAAAASUVORK5CYII="));
        watermarkFile.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        watermarkForm.Add(watermarkFile, "file", "publisher.png");
        var watermarkUpload = await owner.PostAsync($"/api/v1/channels/{channelId}/watermark", watermarkForm);
        var channelWithWatermark = await watermarkUpload.Content.ReadFromJsonAsync<ChannelResponse>(JsonOptions);
        var watermarkReadUrl = channelWithWatermark!.WatermarkUrl;
        Assert.Equal(HttpStatusCode.OK, watermarkUpload.StatusCode);
        Assert.StartsWith("https://storage.test/channel-watermarks/", watermarkReadUrl);

        // Act
        var upload = await UploadAsync(owner, channelId, "Video công khai");
        await using (var db = factory.CreateDb())
        {
            var persisted = await db.Videos.AsNoTracking().SingleAsync(x => x.VideoId == upload.VideoId);
            Assert.Equal(ownerId, persisted.UploadedByUserId);
            Assert.Equal("pending", persisted.ModerationStatus);
            Assert.True(await db.ModerationCases.AnyAsync(x => x.VideoId == upload.VideoId && x.CaseType == "upload_review" && x.Status == "pending"));
        }
        var privateResponse = await factory.CreateClient().GetAsync($"/api/v1/videos/{upload.VideoId}");
        var submit = await owner.PostAsync($"/api/v1/videos/{upload.VideoId}/submit-moderation", null);
        await ApproveAsync(upload.VideoId);
        var publish = await owner.PostAsync($"/api/v1/videos/{upload.VideoId}/publish", null);
        var anonymous = factory.CreateClient();
        await WaitForReadyRenditionsAsync(upload.VideoId, expectedReadyCount: 3);
        var home = await anonymous.GetFromJsonAsync<PageResult<VideoCardResponse>>("/api/v1/feed/home", JsonOptions);
        var explore = await anonymous.GetFromJsonAsync<PageResult<VideoCardResponse>>("/api/v1/feed/explore?sort=newest", JsonOptions);
        var playback = await anonymous.GetFromJsonAsync<PlaybackResponse>($"/api/v1/videos/{upload.VideoId}/playback", JsonOptions);
        var videoDetail = await anonymous.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{upload.VideoId}", JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, privateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Contains(home!.Items, x => x.VideoId == upload.VideoId);
        Assert.Contains(explore!.Items, x => x.VideoId == upload.VideoId);
        Assert.Contains(playback!.Renditions, x => x.Quality == "720p" && x.Url.StartsWith("https://storage.test/"));
        Assert.Contains(playback.Renditions, x => x.Quality == "480p" && x.Url.StartsWith("https://storage.test/"));
        Assert.Contains(playback.Renditions, x => x.Quality == "360p" && x.Url.StartsWith("https://storage.test/"));
        Assert.Equal(watermarkReadUrl, videoDetail!.ChannelWatermarkUrl);
        Assert.NotEmpty(factory.Storage.Objects);
    }

    [Fact]
    public async Task VideoThumbnail_ValidImagePersistsAndInvalidTypeDoesNotMutate()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("thumbnail_endpoint");
        var video = await UploadAsync(owner, channelId, "Thumbnail endpoint video");
        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j8ioAAAAASUVORK5CYII=");

        using (var valid = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(pngBytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            valid.Add(file, "thumbnail", "thumb.png");
            valid.Add(new StringContent("false"), "generate");
            var response = await owner.PostAsync($"/api/v1/videos/{video.VideoId}/thumbnail", valid);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<VideoResponse>(JsonOptions))!;
            Assert.StartsWith("https://storage.test/video-thumbnails/", body.ThumbnailUrl);
        }

        using (var invalid = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("not an image"));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            invalid.Add(file, "thumbnail", "thumb.txt");
            invalid.Add(new StringContent("false"), "generate");
            var response = await owner.PostAsync($"/api/v1/videos/{video.VideoId}/thumbnail", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("INVALID_FILE_TYPE", await response.Content.ReadAsStringAsync());
        }

        await using var db = factory.CreateDb();
        Assert.StartsWith("test://video-thumbnails/",
            (await db.Videos.AsNoTracking().SingleAsync(x => x.VideoId == video.VideoId)).ThumbnailUrl);
    }

    [Fact]
    public async Task VideoInteractions_AreMutuallyExclusiveAndPersistWatchProgressRatingShare()
    {
        // Arrange
        var (owner, _, channelId) = await CreateUserAndChannelAsync("interact");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Video tương tác");

        // Act
        var like = await owner.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/reaction", new { type = "like" });
        var dislike = await owner.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/reaction", new { type = "dislike" });
        var progress = await owner.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/watch-progress", new { watchedSeconds = 60, saveHistory = true });
        var rating = await owner.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/rating", new { score = 5 });
        var share = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/share", new { method = "copy_link" });
        var detail = await owner.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{video.VideoId}", JsonOptions);
        var removeReaction = await owner.DeleteAsync($"/api/v1/videos/{video.VideoId}/reaction");
        var removeRating = await owner.DeleteAsync($"/api/v1/videos/{video.VideoId}/rating");

        // Assert
        Assert.Equal(HttpStatusCode.OK, like.StatusCode);
        var reaction = await dislike.Content.ReadFromJsonAsync<ReactionResponse>(JsonOptions);
        Assert.Equal("dislike", reaction!.MyReaction); Assert.Equal(0, reaction.Likes); Assert.Equal(1, reaction.Dislikes);
        Assert.Equal(50m, (await progress.Content.ReadFromJsonAsync<WatchProgressResponse>(JsonOptions))!.Progress);
        Assert.Equal(5, (await rating.Content.ReadFromJsonAsync<RatingResponse>(JsonOptions))!.MyRating);
        Assert.Equal(1, (await share.Content.ReadFromJsonAsync<ShareResponse>(JsonOptions))!.ShareCount);
        Assert.Equal(60, detail!.ViewerState!.ResumeAtSeconds);
        Assert.Equal("dislike", detail.ViewerState.Reaction);
        Assert.Equal(5, detail.ViewerState.Rating);
        Assert.Null((await removeReaction.Content.ReadFromJsonAsync<ReactionResponse>(JsonOptions))!.MyReaction);
        Assert.Null((await removeRating.Content.ReadFromJsonAsync<RatingResponse>(JsonOptions))!.MyRating);
    }

    [Fact]
    public async Task VideoLikeCommentLikeAndReply_CreateRealtimeNotificationRows()
    {
        var (owner, ownerId, channelId) = await CreateUserAndChannelAsync("interaction_owner");
        var (viewer, _, _) = await CreateUserAndChannelAsync("interaction_viewer");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Video realtime");

        var like = await viewer.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/reaction", new { type = "like" });
        var parentResponse = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Bình luận gốc", parentCommentId = (Guid?)null });
        var parent = await parentResponse.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions);
        var reply = await viewer.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Câu trả lời", parentCommentId = parent!.CommentId });
        var commentLike = await viewer.PutAsJsonAsync($"/api/v1/comments/{parent.CommentId}/reaction", new { type = "like" });

        Assert.Equal(HttpStatusCode.OK, like.StatusCode);
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        Assert.Equal(HttpStatusCode.OK, commentLike.StatusCode);

        await using var db = factory.CreateDb();
        var notifications = await db.Notifications.AsNoTracking().Where(x => x.UserId == ownerId).ToListAsync();
        Assert.Contains(notifications, x => x.Type == "video_like" && x.ResourceId == video.VideoId);
        Assert.Contains(notifications, x => x.Type == "comment_reply" && x.ResourceId == parent.CommentId);
        Assert.Contains(notifications, x => x.Type == "comment_like" && x.ResourceId == parent.CommentId);
    }

    [Fact]
    public async Task Comments_ReplyReactionReportAndOwnerModeration_WorkEndToEnd()
    {
        // Arrange
        var (owner, ownerId, channelId) = await CreateUserAndChannelAsync("comment_owner");
        var (viewer, viewerId, _) = await CreateUserAndChannelAsync("comment_viewer");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Video bình luận");
        Guid violationId;
        await using (var db = factory.CreateDb())
        {
            var violation = new ViolationType { ViolationTypeId = Guid.NewGuid(), Code = "spam", Name = "Spam", Status = "active", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.ViolationTypes.Add(violation); await db.SaveChangesAsync(); violationId = violation.ViolationTypeId;
        }

        // Act
        var parentResponse = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Bình luận gốc", parentCommentId = (Guid?)null });
        var parent = await parentResponse.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions);
        var replyResponse = await viewer.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Nội dung trả lời", parentCommentId = parent!.CommentId });
        var replies = await factory.CreateClient().GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/comments/{parent.CommentId}/replies", JsonOptions);
        var reaction = await viewer.PutAsJsonAsync($"/api/v1/comments/{parent.CommentId}/reaction", new { type = "like" });
        var report = await viewer.PostAsJsonAsync($"/api/v1/comments/{parent.CommentId}/report", new { violationTypeId = violationId, description = "Nội dung spam" });
        var hide = await owner.PatchAsJsonAsync($"/api/v1/comments/{parent.CommentId}/visibility", new { hidden = true, reason = "Kiểm duyệt kênh" });
        var managed = await owner.GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/channels/{channelId}/comments/manage?status=hidden", JsonOptions);
        var reply = await replyResponse.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions);
        await using (var ordering = factory.CreateDb())
        {
            var oldest = await ordering.Comments.SingleAsync(x => x.CommentId == parent!.CommentId);
            var newest = await ordering.Comments.SingleAsync(x => x.CommentId == reply!.CommentId);
            oldest.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            newest.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await ordering.SaveChangesAsync();
        }
        var newestComments = await owner.GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/channels/{channelId}/comments/manage?sort=newest&page=1&pageSize=1", JsonOptions);
        var oldestComments = await owner.GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/channels/{channelId}/comments/manage?sort=oldest&page=1&pageSize=1", JsonOptions);
        var mostLikedComments = await owner.GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/channels/{channelId}/comments/manage?sort=mostLiked&page=1&pageSize=1", JsonOptions);
        var ownerDeletesViewerComment = await owner.DeleteAsync($"/api/v1/comments/{reply!.CommentId}");
        var violationTypes = await factory.CreateClient().GetFromJsonAsync<List<ViolationTypeResponse>>("/api/v1/violation-types", JsonOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Created, parentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replyResponse.StatusCode);
        Assert.Contains(replies!.Items, x => x.ParentCommentId == parent.CommentId);
        Assert.Equal(1, (await reaction.Content.ReadFromJsonAsync<ReactionResponse>(JsonOptions))!.Likes);
        Assert.Equal(HttpStatusCode.Created, report.StatusCode);
        Assert.Equal("hidden", (await hide.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions))!.Status);
        Assert.Contains(managed!.Items, x => x.CommentId == parent.CommentId && x.Status == "hidden" && x.VideoTitle == video.Title);
        Assert.Equal(reply.CommentId, Assert.Single(newestComments!.Items).CommentId);
        Assert.Equal(parent.CommentId, Assert.Single(oldestComments!.Items).CommentId);
        Assert.Equal(parent.CommentId, Assert.Single(mostLikedComments!.Items).CommentId);
        Assert.All(newestComments.Items.Concat(oldestComments.Items).Concat(mostLikedComments.Items), item => Assert.Equal(video.Title, item.VideoTitle));
        Assert.Equal(HttpStatusCode.NoContent, ownerDeletesViewerComment.StatusCode);
        Assert.Contains(violationTypes!, x => x.ViolationTypeId == violationId);
        await using var verify = factory.CreateDb();
        Assert.True(await verify.Notifications.AnyAsync(x => x.UserId == ownerId && x.Type == "comment_reply"));
        var storedReport = await verify.Reports.SingleAsync(x => x.UserId == viewerId && x.CommentId == parent.CommentId);
        Assert.NotNull(storedReport.ModerationCaseId);
        Assert.True(await verify.ModerationCases.AnyAsync(x => x.ModerationCaseId == storedReport.ModerationCaseId && x.CaseType == "report_case"));
    }

    [Fact]
    public async Task SettingsDownloadsAndCreatorManagement_PersistAndEnforceOwnership()
    {
        // Arrange
        var (owner, ownerId, channelId) = await CreateUserAndChannelAsync("manage_owner");
        var (other, _, _) = await CreateUserAndChannelAsync("manage_other");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Video quản lý");
        var subscribe = await owner.PostAsJsonAsync("/api/v1/plans/00000000-0000-0000-0000-000000000101/subscribe", new { autoRenew = false });
        Assert.Equal(HttpStatusCode.PaymentRequired, subscribe.StatusCode);
        await using (var setup = factory.CreateDb())
        {
            var paidPlanId = Guid.Parse("00000000-0000-0000-0000-000000000101");
            (await setup.Users.SingleAsync(x => x.UserId == ownerId)).PlanId = paidPlanId;
            setup.PlanHistories.Add(new HuTube.Domain.Plans.PlanHistory { UserId = ownerId, OwnerUserId = ownerId, PlanId = paidPlanId, EndedAt = DateTimeOffset.UtcNow.AddDays(30), PaymentReference = "test-paid-fixture" });
            await setup.SaveChangesAsync();
        }

        // Act
        var settings = await owner.PutAsJsonAsync("/api/v1/account/settings", new { language = "en", theme = "dark", keepSubscriptionsPrivate = false });
        var options = await owner.GetFromJsonAsync<List<RenditionResponse>>($"/api/v1/videos/{video.VideoId}/download-options", JsonOptions);
        var createDownload = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/downloads", new { quality = "720p" });
        var deniedDownloadOptions = await other.GetAsync($"/api/v1/videos/{video.VideoId}/download-options");
        var download = await createDownload.Content.ReadFromJsonAsync<DownloadResponse>(JsonOptions);
        using var file = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/download-file", new { quality = "720p" });
        var fileBytes = await file.Content.ReadAsByteArrayAsync();
        var deniedFile = await other.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/download-file", new { quality = "720p" });
        var deniedQuality = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/download-file", new { quality = "2160p" });
        using var anonymous = factory.CreateClient();
        var anonymousFile = await anonymous.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/download-file", new { quality = "720p" });
        using var webOptionsRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/videos/{video.VideoId}/download-options");
        webOptionsRequest.Headers.Add("X-HuTube-Client", "web");
        using var webOptionsResponse = await owner.SendAsync(webOptionsRequest);
        var webOptions = await webOptionsResponse.Content.ReadFromJsonAsync<List<RenditionResponse>>(JsonOptions);
        var downloads = await owner.GetFromJsonAsync<List<DownloadResponse>>("/api/v1/downloads", JsonOptions);
        var forbidden = await other.PatchAsJsonAsync($"/api/v1/videos/{video.VideoId}", new { title = "Không được sửa" });
        var manage = await owner.GetFromJsonAsync<PageResult<VideoResponse>>($"/api/v1/videos/manage?channelId={channelId}", JsonOptions);
        var cancelDownload = await owner.PostAsync($"/api/v1/downloads/{download!.VideoDownloadId}/cancel", null);
        var retryDownload = await owner.PostAsync($"/api/v1/downloads/{download.VideoDownloadId}/retry", null);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/downloads") { Content = JsonContent.Create(new { ids = new[] { download.VideoDownloadId } }) };
        var deleteDownload = await owner.SendAsync(deleteRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        await using var db = factory.CreateDb(); var persisted = await db.Users.AsNoTracking().SingleAsync(x => x.UserId == ownerId);
        Assert.Equal("en", persisted.PreferredLanguage); Assert.Equal("dark", persisted.Theme);
        Assert.Contains(options!, x => x.Quality == "720p");
        Assert.All(webOptions!, x => Assert.Equal("", x.Url));
        Assert.All(webOptions!, x => Assert.Equal(".mp4", x.FileExtension));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("video/mp4", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("720p.mp4", file.Content.Headers.ContentDisposition.FileNameStar!);
        Assert.Null(file.Headers.Location);
        Assert.True(file.Headers.CacheControl!.NoStore);
        var storedRendition = await db.VideoRenditions.AsNoTracking().SingleAsync(x => x.VideoId == video.VideoId && x.QualityLabel == "720p");
        Assert.Equal(factory.Storage.Objects[storedRendition.FileUrl], fileBytes);
        Assert.Equal(HttpStatusCode.Forbidden, deniedFile.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedQuality.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousFile.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedDownloadOptions.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createDownload.StatusCode); Assert.Contains(downloads!, x => x.VideoDownloadId == download.VideoDownloadId);
        Assert.Equal("cancelled", (await cancelDownload.Content.ReadFromJsonAsync<DownloadResponse>(JsonOptions))!.Status);
        Assert.Equal("processing", (await retryDownload.Content.ReadFromJsonAsync<DownloadResponse>(JsonOptions))!.Status);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains(manage!.Items, x => x.VideoId == video.VideoId);
        Assert.Equal(HttpStatusCode.NoContent, deleteDownload.StatusCode);
    }

    [Fact]
    public async Task Downloads_AreActorScopedAndDeletingARecordKeepsSourceMedia()
    {
        // Arrange
        var (owner, ownerId, channelId) = await CreateUserAndChannelAsync("download_scope_owner");
        var (other, _, _) = await CreateUserAndChannelAsync("download_scope_other");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Download scope video");
        var paidPlanId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        await using (var setup = factory.CreateDb())
        {
            (await setup.Users.SingleAsync(user => user.UserId == ownerId)).PlanId = paidPlanId;
            setup.PlanHistories.Add(new HuTube.Domain.Plans.PlanHistory
            {
                UserId = ownerId,
                OwnerUserId = ownerId,
                PlanId = paidPlanId,
                Status = "active",
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow.AddDays(30),
                PaymentReference = "download-scope-fixture"
            });
            await setup.SaveChangesAsync();
        }

        var create = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/downloads", new { quality = "720p" });
        var download = (await create.Content.ReadFromJsonAsync<DownloadResponse>(JsonOptions))!;
        await using var mediaDb = factory.CreateDb();
        var sourcePath = await mediaDb.VideoRenditions.AsNoTracking()
            .Where(rendition => rendition.VideoId == video.VideoId && rendition.QualityLabel == "720p")
            .Select(rendition => rendition.FileUrl)
            .SingleAsync();

        // Act
        var otherList = await other.GetFromJsonAsync<List<DownloadResponse>>("/api/v1/downloads", JsonOptions);
        var foreignDelete = await other.DeleteAsync($"/api/v1/downloads/{download.VideoDownloadId}");
        var foreignState = await other.PostAsync($"/api/v1/downloads/{download.VideoDownloadId}/cancel", null);
        using var mixedDelete = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/downloads")
        {
            Content = JsonContent.Create(new { ids = new[] { download.VideoDownloadId } })
        };
        var otherBulkDelete = await other.SendAsync(mixedDelete);
        var ownerDelete = await owner.DeleteAsync($"/api/v1/downloads/{download.VideoDownloadId}");

        // Assert
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Empty(otherList!);
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignState.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, otherBulkDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ownerDelete.StatusCode);
        var remainingDownloads = await owner.GetFromJsonAsync<List<DownloadResponse>>("/api/v1/downloads", JsonOptions);
        Assert.DoesNotContain(remainingDownloads!, item => item.VideoDownloadId == download.VideoDownloadId);
        Assert.Contains(sourcePath, factory.Storage.Objects.Keys);
    }

    [Fact]
    public async Task UploadPreflightRetryAndCancel_EnforcePlanAndRetainMediaUntilPurge()
    {
        // Arrange
        var (owner, _, channelId) = await CreateUserAndChannelAsync("upload_state");

        // Act
        var allowed = await owner.PostAsJsonAsync("/api/v1/videos/upload-preflight", new { channelId, fileSize = 1024, duration = 60, contentType = "video/mp4", sourceQuality = "720p" });
        var denied = await owner.PostAsJsonAsync("/api/v1/videos/upload-preflight", new { channelId, fileSize = 1024, duration = 60, contentType = "video/mp4", sourceQuality = "1080p" });
        var video = await UploadAsync(owner, channelId, "Video retry");
        string storedPath;
        await using (var db = factory.CreateDb())
        {
            storedPath = await db.Videos.Where(x => x.VideoId == video.VideoId).Select(x => x.VideoUrl).SingleAsync();
            await db.Videos.Where(x => x.VideoId == video.VideoId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "failed"));
            await db.VideoRenditions.Where(x => x.VideoId == video.VideoId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "failed"));
        }
        var retry = await owner.PostAsync($"/api/v1/videos/{video.VideoId}/retry-processing", null);
        var cancel = await owner.PostAsync($"/api/v1/videos/{video.VideoId}/cancel-upload", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); Assert.True((await allowed.Content.ReadFromJsonAsync<UploadPreflightResponse>(JsonOptions))!.Allowed);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("processing", (await retry.Content.ReadFromJsonAsync<VideoResponse>(JsonOptions))!.Status);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode); Assert.True(factory.Storage.Objects.ContainsKey(storedPath));
        await using var verify = factory.CreateDb();
        var cancelled = await verify.Videos.SingleAsync(x => x.VideoId == video.VideoId);
        Assert.Equal("deleted", cancelled.Status);
        Assert.NotNull(cancelled.MediaRetentionUntil);
    }

    [Fact]
    public async Task CategoriesCreatorEditFilterAndSoftDelete_WorkEndToEnd()
    {
        // Arrange
        var (owner, _, channelId) = await CreateUserAndChannelAsync("metadata");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var category = new Category { CategoryId = Guid.NewGuid(), Name = $"Giáo dục {suffix}", Slug = $"giao-duc-{suffix}", Status = "active", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await using (var db = factory.CreateDb()) { db.Categories.Add(category); await db.SaveChangesAsync(); }
        var video = await UploadAsync(owner, channelId, "Tiêu đề cũ");

        // Act
        var categories = await factory.CreateClient().GetFromJsonAsync<List<CategoryResponse>>("/api/v1/categories", JsonOptions);
        var update = await owner.PatchAsJsonAsync($"/api/v1/videos/{video.VideoId}", new
        {
            title = "Tiêu đề mới", categoryId = category.CategoryId, visibility = "unlisted",
            tags = new[] { "dotnet", "api" }, chapters = new[] { new { startSeconds = 0, title = "Bắt đầu" } }
        });
        var updated = await update.Content.ReadFromJsonAsync<VideoResponse>(JsonOptions);
        var managed = await owner.GetFromJsonAsync<PageResult<VideoResponse>>($"/api/v1/videos/manage?channelId={channelId}&visibility=unlisted", JsonOptions);
        var delete = await owner.DeleteAsync($"/api/v1/videos/{video.VideoId}");
        var missing = await owner.GetAsync($"/api/v1/videos/{video.VideoId}");

        // Assert
        Assert.Contains(categories!, x => x.CategoryId == category.CategoryId);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode); Assert.Equal("Tiêu đề mới", updated!.Title);
        Assert.Equal(category.CategoryId, updated.CategoryId); Assert.Equal(new[] { "api", "dotnet" }, updated.Tags);
        Assert.Contains(managed!.Items, x => x.VideoId == video.VideoId);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CommentCrudSortAndGuestAuthorization_WorkEndToEnd()
    {
        // Arrange
        var (owner, _, channelId) = await CreateUserAndChannelAsync("comment_crud");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Video CRUD comment");
        var create = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Nội dung ban đầu", parentCommentId = (Guid?)null });
        var comment = await create.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions);

        // Act
        var update = await owner.PatchAsJsonAsync($"/api/v1/comments/{comment!.CommentId}", new { content = "Nội dung đã sửa" });
        await owner.PutAsJsonAsync($"/api/v1/comments/{comment.CommentId}/reaction", new { type = "like" });
        var top = await factory.CreateClient().GetFromJsonAsync<PageResult<CommentResponse>>($"/api/v1/videos/{video.VideoId}/comments?sort=top", JsonOptions);
        var removeReaction = await owner.DeleteAsync($"/api/v1/comments/{comment.CommentId}/reaction");
        var delete = await owner.DeleteAsync($"/api/v1/comments/{comment.CommentId}");
        var guest = factory.CreateClient();
        var guestComment = await guest.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Không đăng nhập", parentCommentId = (Guid?)null });
        var guestReaction = await guest.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/reaction", new { type = "like" });

        // Assert
        Assert.Equal("Nội dung đã sửa", (await update.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions))!.Content);
        Assert.Contains(top!.Items, x => x.CommentId == comment.CommentId && x.Likes == 1);
        Assert.Equal(HttpStatusCode.OK, removeReaction.StatusCode); Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, guestComment.StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, guestReaction.StatusCode);
    }

    [Fact]
    public async Task PrivatePlaylistItems_AreTombstonesForOtherViewers()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("playlist_security");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Confidential title");
        var created = await owner.PostAsJsonAsync("/api/v1/playlists", new CreatePlaylistRequest("Public collection", Visibility: "public"));
        created.EnsureSuccessStatusCode();
        var playlist = (await created.Content.ReadFromJsonAsync<PlaylistResponse>(JsonOptions))!;
        (await owner.PostAsJsonAsync($"/api/v1/playlists/{playlist.PlaylistId}/videos", new AddPlaylistVideoRequest(video.VideoId))).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/v1/videos/{video.VideoId}", new { visibility = "private" })).EnsureSuccessStatusCode();
        var publicView = (await factory.CreateClient().GetFromJsonAsync<PlaylistResponse>($"/api/v1/playlists/{playlist.PlaylistId}", JsonOptions))!;
        var item = Assert.Single(publicView.Items);
        Assert.False(item.Available); Assert.Null(item.Title); Assert.Null(item.ThumbnailUrl);
        Assert.Null(item.Visibility); Assert.Null(item.Status); Assert.Null(item.ModerationStatus); Assert.Equal(0, item.Duration);
        var ownerView = (await owner.GetFromJsonAsync<PlaylistResponse>($"/api/v1/playlists/{playlist.PlaylistId}", JsonOptions))!;
        Assert.Equal(video.Title, Assert.Single(ownerView.Items).Title);
    }

    [Fact]
    public async Task MetadataDoesNotIssueSourceUrls_AndSuspendedChannelsCannotPlay()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("media_security");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Access policy video");
        var anonymous = factory.CreateClient();
        var detail = (await anonymous.GetFromJsonAsync<VideoResponse>($"/api/v1/videos/{video.VideoId}", JsonOptions))!;
        Assert.Equal("", detail.VideoUrl);
        Assert.NotEmpty((await anonymous.GetFromJsonAsync<PlaybackResponse>($"/api/v1/videos/{video.VideoId}/playback", JsonOptions))!.Renditions);
        await using (var db = factory.CreateDb())
            await db.Channels.Where(x => x.ChannelId == channelId).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "suspended"));
        Assert.False((await anonymous.GetAsync($"/api/v1/videos/{video.VideoId}")).IsSuccessStatusCode);
        Assert.False((await anonymous.GetAsync($"/api/v1/videos/{video.VideoId}/playback")).IsSuccessStatusCode);
        var search = (await anonymous.GetFromJsonAsync<PageResult<VideoCardResponse>>($"/api/v1/videos/search?channelId={channelId}", JsonOptions))!;
        Assert.DoesNotContain(search.Items, item => item.VideoId == video.VideoId);
    }

    [Fact]
    public async Task ProfileBioPersists_AndModeratorRestrictionsSurviveCreatorEdits()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("moderation_security");
        (await owner.PatchAsJsonAsync("/api/v1/account/profile", new { bio = "Persistent biography" })).EnsureSuccessStatusCode();
        var profile = (await owner.GetFromJsonAsync<UserProfileDto>("/api/v1/account/profile", JsonOptions))!;
        Assert.Equal("Persistent biography", profile.Bio);
        var video = await CreatePublishedVideoAsync(owner, channelId, "Restricted video");
        await using (var db = factory.CreateDb())
        {
            var stored = await db.Videos.SingleAsync(x => x.VideoId == video.VideoId);
            stored.ModerationAgeRestricted = true; stored.AgeRestricted = true;
            stored.RecommendationRestricted = true;
            stored.Metadata = "{\"chapters\":[],\"recommendation_restricted\":true}";
            await db.SaveChangesAsync();
        }
        (await owner.PatchAsJsonAsync($"/api/v1/videos/{video.VideoId}", new { ageRestricted = false, chapters = new[] { new { startSeconds = 0, title = "Intro" } } })).EnsureSuccessStatusCode();
        await using var verify = factory.CreateDb();
        var persisted = await verify.Videos.AsNoTracking().SingleAsync(x => x.VideoId == video.VideoId);
        Assert.True(persisted.AgeRestricted); Assert.True(persisted.RecommendationRestricted);
        using var metadata = JsonDocument.Parse(persisted.Metadata);
        Assert.True(metadata.RootElement.GetProperty("recommendation_restricted").GetBoolean());
        var search = (await factory.CreateClient().GetFromJsonAsync<PageResult<VideoCardResponse>>($"/api/v1/videos/search?channelId={channelId}", JsonOptions))!;
        Assert.DoesNotContain(search.Items, item => item.VideoId == video.VideoId);
        await verify.Videos.Where(x => x.VideoId == video.VideoId).ExecuteUpdateAsync(update => update.SetProperty(x => x.ModerationHidden, true).SetProperty(x => x.Visibility, "private"));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PatchAsJsonAsync($"/api/v1/videos/{video.VideoId}", new { visibility = "public" })).StatusCode);
    }

    [Fact]
    public async Task DisabledComments_RejectNewComments()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("comments_disabled");
        var video = await CreatePublishedVideoAsync(owner, channelId, "No comments");
        await using (var db = factory.CreateDb())
            await db.Videos.Where(x => x.VideoId == video.VideoId).ExecuteUpdateAsync(update => update.SetProperty(x => x.AllowComments, false));
        var response = await owner.PostAsJsonAsync($"/api/v1/videos/{video.VideoId}/comments", new { content = "Should be refused" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static SepayOptions PaymentOptions() => new() { SecretKey = "local-test", AccountNumber = "local-test-account", BankCode = "test-bank" };

    [Theory]
    [InlineData("cancelled", false)]
    [InlineData("failed", false)]
    [InlineData("paid", false)]
    [InlineData("pending", true)]
    public async Task PaymentIdempotency_IsScopedToUserAndRejectsFinishedAttempts(string status, bool expired)
    {
        var (_, firstUser, _) = await CreateUserAndChannelAsync("payment_a");
        var (_, secondUser, _) = await CreateUserAndChannelAsync("payment_b");
        var planId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        async Task<CreatePaymentResponse> Create(Guid userId, bool autoRenew = false, string key = "shared-key")
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
            var service = new PaymentService(db, scope.ServiceProvider.GetRequiredService<IPlanService>(), PaymentOptions(), TimeProvider.System);
            return await service.InitiateAsync(userId, new CreatePaymentRequest(planId, autoRenew, key));
        }
        var simultaneous = await Task.WhenAll(Create(firstUser), Create(firstUser));
        Assert.Equal(simultaneous[0].PaymentId, simultaneous[1].PaymentId);
        Assert.NotEqual(simultaneous[0].PaymentId, (await Create(secondUser)).PaymentId);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5);
        DateTimeOffset? paidAt = status == "paid" ? DateTimeOffset.UtcNow : null;
        await using (var db = factory.CreateDb())
            await db.Payments.Where(x => x.PaymentId == simultaneous[0].PaymentId).ExecuteUpdateAsync(x => x
                .SetProperty(p => p.Status, status).SetProperty(p => p.ExpiresAt, expiresAt).SetProperty(p => p.PaidAt, paidAt));
        var finished = await Assert.ThrowsAsync<PaymentException>(() => Create(firstUser));
        Assert.Equal(409, finished.Status);
        Assert.Equal("PAYMENT_REQUEST_ALREADY_PROCESSED", finished.Code);
        var conflict = await Assert.ThrowsAsync<PaymentException>(() => Create(firstUser, true));
        Assert.Equal(409, conflict.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        var fresh = await Create(firstUser, key: "fresh-key");
        Assert.NotEqual(simultaneous[0].PaymentId, fresh.PaymentId);
        await using var verify = factory.CreateDb();
        Assert.Equal(1, await verify.Payments.CountAsync(x => x.UserId == firstUser && x.IdempotencyKey == "shared-key"));
        Assert.Equal(status, (await verify.Payments.SingleAsync(x => x.PaymentId == simultaneous[0].PaymentId)).Status);
    }

    [Fact]
    public async Task PaymentFinalSaveFailure_RollsBackActivatedPlanAndHistory()
    {
        var (_, userId, _) = await CreateUserAndChannelAsync("payment_atomic");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HuTubeDbContext>();
        var service = new PaymentService(db, scope.ServiceProvider.GetRequiredService<IPlanService>(), PaymentOptions(), TimeProvider.System);
        var planId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var payment = await service.InitiateAsync(userId, new CreatePaymentRequest(planId));
        var previousPlan = await db.Users.Where(x => x.UserId == userId).Select(x => x.PlanId).SingleAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION bugfix_reject_paid() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.status = 'paid' THEN RAISE EXCEPTION 'injected final-save failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER bugfix_reject_paid BEFORE UPDATE ON payments FOR EACH ROW EXECUTE FUNCTION bugfix_reject_paid();");
        try
        {
            var payload = new SepayWebhookPayload(987654321, "test-bank", "2026-10-02", "local-test-account", null,
                payment.TransactionCode, payment.TransactionCode, "in", 99000, 99000, "local-test-reference");
            await Assert.ThrowsAsync<DbUpdateException>(() => service.HandleSepayWebhookAsync(payload));
            await using var verify = factory.CreateDb();
            Assert.Equal(previousPlan, await verify.Users.Where(x => x.UserId == userId).Select(x => x.PlanId).SingleAsync());
            Assert.Equal("pending", await verify.Payments.Where(x => x.PaymentId == payment.PaymentId).Select(x => x.Status).SingleAsync());
            Assert.False(await verify.PlanHistories.AnyAsync(x => x.UserId == userId && x.PlanId == planId));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER bugfix_reject_paid ON payments; DROP FUNCTION bugfix_reject_paid();"); }
    }

    [Fact]
    public async Task DeletedVideoPurge_PreservesDatabaseConstraintsAndReleasesQuotaOnce()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("purge_regression");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Purge regression");
        (await owner.DeleteAsync($"/api/v1/videos/{video.VideoId}")).EnsureSuccessStatusCode();
        long storageBefore;
        await using (var db = factory.CreateDb())
        {
            storageBefore = await db.ChannelQuotas.Where(x => x.ChannelId == channelId).Select(x => x.StorageUsed).SingleAsync();
            await db.Videos.Where(x => x.VideoId == video.VideoId).ExecuteUpdateAsync(x => x.SetProperty(v => v.MediaRetentionUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        async Task Clean()
        {
            using var worker = new VideoMediaRetentionCleanupService(factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
                factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VideoMediaRetentionCleanupService>>());
            await worker.StartAsync(CancellationToken.None);
            for (var i = 0; i < 100; i++)
            {
                await using var check = factory.CreateDb();
                if (await check.Videos.AnyAsync(x => x.VideoId == video.VideoId && x.MediaPurgedAt != null)) break;
                await Task.Delay(50);
            }
            await worker.StopAsync(CancellationToken.None);
        }
        await Clean();
        await Clean();
        await using var verify = factory.CreateDb();
        var persisted = await verify.Videos.SingleAsync(x => x.VideoId == video.VideoId);
        Assert.NotNull(persisted.MediaPurgedAt);
        Assert.NotEmpty(persisted.VideoUrl);
        Assert.Equal(Math.Max(0, storageBefore - video.FileSize), await verify.ChannelQuotas.Where(x => x.ChannelId == channelId).Select(x => x.StorageUsed).SingleAsync());
        Assert.All(await verify.VideoRenditions.Where(x => x.VideoId == video.VideoId).ToListAsync(), x => Assert.Equal("deleted", x.Status));
    }

    [Fact]
    public async Task ConcurrentUploads_ReserveQuotaUsingFreshValues_AndCleanRejectedObjects()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("quota_race");
        var bytes = Encoding.UTF8.GetBytes("fake-video-content");
        await using (var db = factory.CreateDb())
            await db.ChannelQuotas.Where(x => x.ChannelId == channelId).ExecuteUpdateAsync(x => x.SetProperty(q => q.StorageLimit, bytes.Length + 1L));
        var entered = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storageBefore = factory.Storage.Objects.Keys.ToHashSet();
        factory.Storage.BeforeSave = async (folder, fileName) => {
            if (folder != "videos" || !fileName.StartsWith("quota-")) return;
            if (Interlocked.Increment(ref entered) == 2) barrier.TrySetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(15));
        };
        async Task<HttpResponseMessage> Upload(string name)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(channelId.ToString()), "ChannelId");
            form.Add(new StringContent(name), "Title");
            form.Add(new StringContent("120"), "Duration");
            form.Add(new StringContent("720p"), "SourceQuality");
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            form.Add(content, "Video", name + ".mp4");
            return await owner.PostAsync("/api/v1/videos", form);
        }
        try
        {
            var responses = await Task.WhenAll(Upload("quota-first"), Upload("quota-second"));
            Assert.Single(responses, x => x.IsSuccessStatusCode);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            await using var verify = factory.CreateDb();
            Assert.Equal(bytes.Length, await verify.ChannelQuotas.Where(x => x.ChannelId == channelId).Select(x => x.StorageUsed).SingleAsync());
            Assert.Equal(1, await verify.Videos.CountAsync(x => x.ChannelId == channelId));
            Assert.Single(factory.Storage.Objects.Keys, x => !storageBefore.Contains(x) && x.StartsWith("test://videos/"));
            foreach (var response in responses) response.Dispose();
        }
        finally { factory.Storage.BeforeSave = null; }
    }

    [Fact]
    public async Task UploadProbeUsesActualDuration_AndHonorsLowerSourceQuality()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("media_probe");
        async Task<HttpResponseMessage> Upload(int actualDuration)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(channelId.ToString()), "ChannelId");
            form.Add(new StringContent("Probed metadata"), "Title");
            form.Add(new StringContent("1"), "Duration");
            form.Add(new StringContent("360p"), "SourceQuality");
            var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes($"fake-video-duration:{actualDuration}"));
            bytes.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            form.Add(bytes, "Video", "probe.mp4");
            return await owner.PostAsync("/api/v1/videos", form);
        }
        var response = await Upload(120); response.EnsureSuccessStatusCode();
        var video = (await response.Content.ReadFromJsonAsync<VideoResponse>(JsonOptions))!;
        Assert.Equal(120, video.Duration);
        await using var verify = factory.CreateDb();
        Assert.Equal(360, await verify.VideoRenditions.Where(x => x.VideoId == video.VideoId).OrderByDescending(x => x.Height).Select(x => x.Height).FirstAsync());
        var denied = await Upload(int.MaxValue);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("VIDEO_TOO_LONG", await denied.Content.ReadAsStringAsync());
    }

    private async Task<(HttpClient Client, Guid UserId, Guid ChannelId)> CreateUserAndChannelAsync(string prefix)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-HuTube-Client", "web");
        var suffix = Guid.NewGuid().ToString("N"); var email = $"{prefix}_{suffix}@example.com"; var username = $"{prefix}_{suffix}"[..Math.Min(40, prefix.Length + 1 + suffix.Length)];
        (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(username, email, "Test User", "Password123!"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new TokenRequest(factory.Emails.Token(email)))).EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Password123!", "web", "Test")); login.EnsureSuccessStatusCode();
        var auth = await login.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        Guid userId; await using (var db = factory.CreateDb()) userId = await db.Users.Where(x => x.Email == email).Select(x => x.UserId).SingleAsync();
        var channelResponse = await client.PostAsJsonAsync("/api/v1/channels", new CreateChannelRequest($"Kênh {prefix}", $"{prefix}-{suffix}"[..Math.Min(48, prefix.Length + 1 + suffix.Length)], "Kênh kiểm thử"));
        channelResponse.EnsureSuccessStatusCode(); var channel = await channelResponse.Content.ReadFromJsonAsync<ChannelResponse>(JsonOptions);
        return (client, userId, channel!.ChannelId);
    }

    private async Task<VideoResponse> UploadAsync(HttpClient client, Guid channelId, string title)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(channelId.ToString()), "ChannelId"); form.Add(new StringContent(title), "Title");
        form.Add(new StringContent("Mô tả video"), "Description"); form.Add(new StringContent("public"), "Visibility");
        form.Add(new StringContent("120"), "Duration"); form.Add(new StringContent("hu-tube"), "Tags");
        form.Add(new StringContent("[{\"startSeconds\":0,\"title\":\"Mở đầu\"},{\"startSeconds\":60,\"title\":\"Phần hai\"}]", Encoding.UTF8), "ChaptersJson");
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-video-content")); bytes.Headers.ContentType = new MediaTypeHeaderValue("video/mp4"); form.Add(bytes, "Video", "sample.mp4");
        var response = await client.PostAsync("/api/v1/videos", form); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VideoResponse>(JsonOptions))!;
    }

    private async Task<VideoResponse> CreatePublishedVideoAsync(HttpClient client, Guid channelId, string title)
    {
        var video = await UploadAsync(client, channelId, title); await client.PostAsync($"/api/v1/videos/{video.VideoId}/submit-moderation", null);
        await ApproveAsync(video.VideoId); var response = await client.PostAsync($"/api/v1/videos/{video.VideoId}/publish", null); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VideoResponse>(JsonOptions))!;
    }

    [Fact]
    public async Task AccountPreferences_RoundTripPartialUpdatesAndRemainUserScoped()
    {
        var (owner, ownerId, _) = await CreateUserAndChannelAsync("preferences_owner");
        var (other, otherId, _) = await CreateUserAndChannelAsync("preferences_other");

        var defaults = await owner.GetFromJsonAsync<UserPreferencesDto>("/api/v1/account/settings", JsonOptions);
        Assert.Equal(new UserPreferencesDto("vi", "system", true, true, "Việt Nam"), defaults);

        var update = await owner.PutAsJsonAsync("/api/v1/account/settings", new
        {
            language = "en",
            theme = "dark",
            keepSubscriptionsPrivate = false,
            keepPlaylistsPrivate = false,
            location = "  Hà Nội 🏙️  "
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var expected = new UserPreferencesDto("en", "dark", false, false, "Hà Nội 🏙️");
        Assert.Equal(expected, await update.Content.ReadFromJsonAsync<UserPreferencesDto>(JsonOptions));
        Assert.Equal(expected, await owner.GetFromJsonAsync<UserPreferencesDto>("/api/v1/account/settings", JsonOptions));

        var clearLocationOnly = await owner.PutAsJsonAsync("/api/v1/account/settings", new { location = "" });
        Assert.Equal(HttpStatusCode.OK, clearLocationOnly.StatusCode);
        Assert.Equal(new UserPreferencesDto("en", "dark", false, false, null),
            await clearLocationOnly.Content.ReadFromJsonAsync<UserPreferencesDto>(JsonOptions));
        Assert.Equal(new UserPreferencesDto("vi", "system", true, true, "Việt Nam"),
            await other.GetFromJsonAsync<UserPreferencesDto>("/api/v1/account/settings", JsonOptions));

        await using var db = factory.CreateDb();
        var persisted = await db.Users.AsNoTracking().SingleAsync(x => x.UserId == ownerId);
        Assert.Equal("en", persisted.PreferredLanguage);
        Assert.Equal("dark", persisted.Theme);
        Assert.False(persisted.KeepSubscriptionsPrivate);
        Assert.False(persisted.KeepPlaylistsPrivate);
        Assert.Null(persisted.Location);
        var untouched = await db.Users.AsNoTracking().SingleAsync(x => x.UserId == otherId);
        Assert.Equal("vi", untouched.PreferredLanguage);
        Assert.Equal("Việt Nam", untouched.Location);
    }

    [Theory]
    [InlineData("language", "fr", "INVALID_LANGUAGE")]
    [InlineData("theme", "neon", "INVALID_THEME")]
    public async Task AccountPreferences_InvalidEnumLeavesPersistedPreferencesUnchanged(string field, string value, string errorCode)
    {
        var (owner, ownerId, _) = await CreateUserAndChannelAsync("preferences_invalid");
        await owner.PutAsJsonAsync("/api/v1/account/settings", new { language = "en", theme = "dark" });
        var response = await owner.PutAsJsonAsync("/api/v1/account/settings", new Dictionary<string, object?> { [field] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(errorCode, await response.Content.ReadAsStringAsync());
        await using var db = factory.CreateDb();
        var persisted = await db.Users.AsNoTracking().SingleAsync(x => x.UserId == ownerId);
        Assert.Equal("en", persisted.PreferredLanguage);
        Assert.Equal("dark", persisted.Theme);
    }

    [Fact]
    public async Task AccountNotificationSettings_RoundTripAllFieldsAndRemainUserScoped()
    {
        var (owner, ownerId, _) = await CreateUserAndChannelAsync("notification_settings_owner");
        var (other, _, _) = await CreateUserAndChannelAsync("notification_settings_other");
        var expected = new NotificationSettingsDto(
            InAppEnabled: false,
            EmailEnabled: true,
            NewVideoEnabled: false,
            CommentReplyEnabled: true,
            ReportResultEnabled: false,
            ModerationEnabled: true,
            PlanEnabled: false,
            RecommendationEnabled: true,
            MentionEnabled: false,
            ChannelActivityEnabled: true,
            PaymentEnabled: false);
        var response = await owner.PutAsJsonAsync("/api/v1/account/notifications", expected);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadFromJsonAsync<NotificationSettingsDto>(JsonOptions));
        Assert.Equal(expected, await owner.GetFromJsonAsync<NotificationSettingsDto>("/api/v1/account/notifications", JsonOptions));
        Assert.Equal(new NotificationSettingsDto(true, true, true, true, true, true, true, true, true, true, true),
            await other.GetFromJsonAsync<NotificationSettingsDto>("/api/v1/account/notifications", JsonOptions));

        await using var db = factory.CreateDb();
        var stored = await db.NotificationSettings.AsNoTracking().SingleAsync(x => x.UserId == ownerId);
        Assert.Equal(expected, new NotificationSettingsDto(
            stored.InAppEnabled, stored.EmailEnabled, stored.NewVideoEnabled, stored.CommentReplyEnabled,
            stored.ReportResultEnabled, stored.ModerationEnabled, stored.PlanEnabled, stored.RecommendationEnabled,
            stored.MentionEnabled, stored.ChannelActivityEnabled, stored.PaymentEnabled));
    }

    [Fact]
    public async Task AccountProfile_PatchIsPartialTrimmedAndDoesNotExposeCredentials()
    {
        var (owner, ownerId, _) = await CreateUserAndChannelAsync("profile_patch");
        var before = await owner.GetFromJsonAsync<UserProfileDto>("/api/v1/account/profile", JsonOptions);
        var response = await owner.PatchAsJsonAsync("/api/v1/account/profile", new
        {
            displayName = "  Tên mới  ",
            bio = "  Nội dung có dấu 🌱  "
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions))!;
        Assert.Equal(ownerId, updated.UserId);
        Assert.Equal(before!.Username, updated.Username);
        Assert.Equal(before.Email, updated.Email);
        Assert.Equal(before.AvatarUrl, updated.AvatarUrl);
        Assert.Equal("Tên mới", updated.DisplayName);
        Assert.Equal("Nội dung có dấu 🌱", updated.Bio);
        var rawProfile = await owner.GetStringAsync("/api/v1/account/profile");
        Assert.DoesNotContain("passwordHash", rawProfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", rawProfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("emailVerificationToken", rawProfile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AccountAvatar_ValidImagePersistsAndInvalidMediaDoesNotCreateObject()
    {
        var (owner, ownerId, _) = await CreateUserAndChannelAsync("avatar_upload");
        using var validForm = new MultipartFormDataContent();
        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j8ioAAAAASUVORK5CYII=");
        var validFile = new ByteArrayContent(pngBytes);
        validFile.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        validForm.Add(validFile, "file", "avatar.png");
        var upload = await owner.PostAsync("/api/v1/account/avatar", validForm);

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var profile = (await upload.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions))!;
        Assert.Equal(ownerId, profile.UserId);
        Assert.StartsWith("test://avatars/", profile.AvatarUrl);
        Assert.Equal(pngBytes, factory.Storage.Objects[profile.AvatarUrl!]);
        var objectsAfterValidUpload = factory.Storage.Objects.Count;

        using var invalidForm = new MultipartFormDataContent();
        var invalidFile = new ByteArrayContent(Encoding.UTF8.GetBytes("not an image"));
        invalidFile.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        invalidForm.Add(invalidFile, "file", "avatar.txt");
        var rejected = await owner.PostAsync("/api/v1/account/avatar", invalidForm);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("INVALID_FILE_TYPE", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(objectsAfterValidUpload, factory.Storage.Objects.Count);
        Assert.Equal(profile, await owner.GetFromJsonAsync<UserProfileDto>("/api/v1/account/profile", JsonOptions));
    }

    [Fact]
    public async Task AccountProfile_DisplayNameBoundsAndHtmlBioAreValidatedWithoutPartialMutation()
    {
        var (owner, _, _) = await CreateUserAndChannelAsync("profile_bounds");
        var original = await owner.GetFromJsonAsync<UserProfileDto>("/api/v1/account/profile", JsonOptions);

        var update = await owner.PatchAsJsonAsync("/api/v1/account/profile", new
        {
            displayName = "  Tên hiển thị 🌱  ",
            bio = "<b>literal</b> & không thực thi"
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions))!;
        Assert.Equal("Tên hiển thị 🌱", updated.DisplayName);
        Assert.Equal("<b>literal</b> & không thực thi", updated.Bio);

        foreach (var invalidName in new[] { "", "   ", new string('x', 121) })
        {
            var rejected = await owner.PatchAsJsonAsync("/api/v1/account/profile", new { displayName = invalidName });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("INVALID_DISPLAY_NAME", await rejected.Content.ReadAsStringAsync());
        }

        var persisted = await owner.GetFromJsonAsync<UserProfileDto>("/api/v1/account/profile", JsonOptions);
        Assert.NotNull(persisted);
        Assert.Equal(updated.DisplayName, persisted.DisplayName);
        Assert.Equal(updated.Bio, persisted.Bio);
        Assert.Equal(original!.Email, persisted.Email);
        Assert.Equal(original.UserId, persisted.UserId);
    }

    [Fact]
    public async Task AccountProfile_ConcurrentDifferentFieldPatchesPreserveBothFields()
    {
        var (owner, _, _) = await CreateUserAndChannelAsync("profile_concurrent");
        var responses = await Task.WhenAll(
            owner.PatchAsJsonAsync("/api/v1/account/profile", new { displayName = "Concurrent name" }),
            owner.PatchAsJsonAsync("/api/v1/account/profile", new { bio = "Concurrent bio" }));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var profile = await owner.GetFromJsonAsync<UserProfileDto>("/api/v1/account/profile", JsonOptions);
        Assert.NotNull(profile);
        Assert.Equal("Concurrent name", profile.DisplayName);
        Assert.Equal("Concurrent bio", profile.Bio);
    }

    [Fact]
    public async Task AccountAvatar_MissingOversizedAndStorageFailureDoNotChangeProfile()
    {
        var (owner, _, _) = await CreateUserAndChannelAsync("avatar_failures");
        var missing = await owner.PostAsync("/api/v1/account/avatar", null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("INVALID_FILE", await missing.Content.ReadAsStringAsync());

        using var oversized = new MultipartFormDataContent();
        var largeFile = new ByteArrayContent(new byte[(5 * 1024 * 1024) + 1]);
        largeFile.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        oversized.Add(largeFile, "file", "large.png");
        var tooLarge = await owner.PostAsync("/api/v1/account/avatar", oversized);
        Assert.True(tooLarge.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
            $"Unexpected oversized avatar status: {tooLarge.StatusCode}");
        if (tooLarge.StatusCode == HttpStatusCode.BadRequest)
            Assert.Contains("FILE_TOO_LARGE", await tooLarge.Content.ReadAsStringAsync());

        factory.Storage.BeforeSave = (_, _) => throw new IOException("controlled storage failure");
        try
        {
            using var failedForm = new MultipartFormDataContent();
            var png = new ByteArrayContent(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j8ioAAAAASUVORK5CYII="));
            png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            failedForm.Add(png, "file", "failed.png");
            var failed = await owner.PostAsync("/api/v1/account/avatar", failedForm);
            Assert.False(failed.IsSuccessStatusCode);
            Assert.Null((await owner.GetFromJsonAsync<UserProfileDto>("/api/v1/account/profile", JsonOptions))!.AvatarUrl);
        }
        finally
        {
            factory.Storage.BeforeSave = null;
        }
    }

    [Fact]
    public async Task Playlist_LifecycleVisibilityOwnershipDuplicateAndAtomicOrderAreEnforced()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("playlist_lifecycle");
        var (other, _, _) = await CreateUserAndChannelAsync("playlist_other");
        var firstVideo = await CreatePublishedVideoAsync(owner, channelId, "Playlist first");
        var secondVideo = await CreatePublishedVideoAsync(owner, channelId, "Playlist second");
        var guest = factory.CreateClient();

        var create = await owner.PostAsJsonAsync("/api/v1/playlists", new CreatePlaylistRequest("  Saved videos  ", "  mô tả  ", "private"));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var playlist = (await create.Content.ReadFromJsonAsync<PlaylistResponse>(JsonOptions))!;
        Assert.Equal("Saved videos", playlist.Name);
        Assert.Equal("private", playlist.Visibility);
        Assert.False((await guest.GetAsync($"/api/v1/playlists/{playlist.PlaylistId}")).IsSuccessStatusCode);

        var addFirst = await owner.PostAsJsonAsync($"/api/v1/playlists/{playlist.PlaylistId}/videos", new AddPlaylistVideoRequest(firstVideo.VideoId));
        var addSecond = await owner.PostAsJsonAsync($"/api/v1/playlists/{playlist.PlaylistId}/videos", new AddPlaylistVideoRequest(secondVideo.VideoId));
        Assert.Equal(HttpStatusCode.OK, addFirst.StatusCode);
        Assert.Equal(HttpStatusCode.OK, addSecond.StatusCode);
        var duplicate = await owner.PostAsJsonAsync($"/api/v1/playlists/{playlist.PlaylistId}/videos", new AddPlaylistVideoRequest(firstVideo.VideoId));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("PLAYLIST_VIDEO_EXISTS", await duplicate.Content.ReadAsStringAsync());

        var invalidOrder = await owner.PutAsJsonAsync($"/api/v1/playlists/{playlist.PlaylistId}/order", new ReorderPlaylistRequest([firstVideo.VideoId, firstVideo.VideoId]));
        Assert.Equal(HttpStatusCode.BadRequest, invalidOrder.StatusCode);
        Assert.Contains("INVALID_PLAYLIST_ORDER", await invalidOrder.Content.ReadAsStringAsync());
        var reordered = await owner.PutAsJsonAsync($"/api/v1/playlists/{playlist.PlaylistId}/order", new ReorderPlaylistRequest([secondVideo.VideoId, firstVideo.VideoId]));
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
        Assert.Equal(secondVideo.VideoId, (await reordered.Content.ReadFromJsonAsync<PlaylistResponse>(JsonOptions))!.Items[0].VideoId);

        var update = await owner.PatchAsJsonAsync($"/api/v1/playlists/{playlist.PlaylistId}", new UpdatePlaylistRequest("Public saved", "Description", "public"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var publicView = await guest.GetFromJsonAsync<PlaylistResponse>($"/api/v1/playlists/{playlist.PlaylistId}", JsonOptions);
        Assert.NotNull(publicView);
        Assert.Equal("public", publicView.Visibility);
        Assert.Equal(2, publicView.Items.Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PatchAsJsonAsync($"/api/v1/playlists/{playlist.PlaylistId}", new UpdatePlaylistRequest("No", null, "public"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/playlists/{playlist.PlaylistId}/videos/{firstVideo.VideoId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/v1/playlists/{playlist.PlaylistId}/videos/{firstVideo.VideoId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/playlists/{playlist.PlaylistId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/v1/playlists/{playlist.PlaylistId}")).StatusCode);
    }

    [Fact]
    public async Task Library_HistoryFiltersApplyBeforePagingAndKeepActorScope()
    {
        var (owner, ownerId, channelId) = await CreateUserAndChannelAsync("history_filters");
        var (other, _, _) = await CreateUserAndChannelAsync("history_filters_other");
        var first = await CreatePublishedVideoAsync(owner, channelId, "Lessons Alpha");
        var second = await CreatePublishedVideoAsync(owner, channelId, "Lessons Beta");
        var old = await CreatePublishedVideoAsync(owner, channelId, "Lessons Old");
        var now = DateTimeOffset.UtcNow;
        await using (var db = factory.CreateDb())
        {
            db.ViewingHistories.AddRange(
                new ViewingHistory { UserId = ownerId, VideoId = first.VideoId, ViewedAt = now.AddDays(-3), WatchDuration = 10 },
                new ViewingHistory { UserId = ownerId, VideoId = first.VideoId, ViewedAt = now.AddDays(-2), WatchDuration = 20 },
                new ViewingHistory { UserId = ownerId, VideoId = second.VideoId, ViewedAt = now.AddDays(-1), WatchDuration = 30 },
                new ViewingHistory { UserId = ownerId, VideoId = old.VideoId, ViewedAt = now.AddDays(-15), WatchDuration = 40 });
            await db.SaveChangesAsync();
        }
        var filtered = await owner.GetFromJsonAsync<PageResult<WatchHistoryResponse>>(
            "/api/v1/library/history?q=LESSONS&sort=oldest&dateRange=week&pageSize=1&page=1", JsonOptions);
        Assert.NotNull(filtered);
        Assert.Equal(2, filtered.Total);
        Assert.Equal(first.VideoId, Assert.Single(filtered.Items).VideoId);
        Assert.Equal(20, filtered.Items[0].WatchedSeconds);
        var secondPage = await owner.GetFromJsonAsync<PageResult<WatchHistoryResponse>>(
            "/api/v1/library/history?q=lessons&sort=oldest&dateRange=week&pageSize=1&page=2", JsonOptions);
        Assert.Equal(second.VideoId, Assert.Single(secondPage!.Items).VideoId);
        var newest = await owner.GetFromJsonAsync<PageResult<WatchHistoryResponse>>(
            "/api/v1/library/history?q=Lessons&dateRange=week", JsonOptions);
        Assert.Equal(second.VideoId, newest!.Items[0].VideoId);
        var baseline = await owner.GetFromJsonAsync<PageResult<WatchHistoryResponse>>("/api/v1/library/history", JsonOptions);
        Assert.Equal(3, baseline!.Total);
        var today = await owner.GetFromJsonAsync<PageResult<WatchHistoryResponse>>("/api/v1/library/history?dateRange=today", JsonOptions);
        Assert.Empty(today!.Items);
        var otherHistory = await other.GetFromJsonAsync<PageResult<WatchHistoryResponse>>("/api/v1/library/history?q=Lessons&dateRange=month", JsonOptions);
        Assert.Empty(otherHistory!.Items);
        var noMatch = await owner.GetFromJsonAsync<PageResult<WatchHistoryResponse>>("/api/v1/library/history?q=notpresent", JsonOptions);
        Assert.Equal(0, noMatch!.Total);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/library/history?sort=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/v1/library/history?dateRange=invalid")).StatusCode);
    }

    [Fact]
    public async Task Library_HistoryAndLikedAreActorScopedAndDeleteIsIdempotent()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("library_owner");
        var (other, _, _) = await CreateUserAndChannelAsync("library_other");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Library video");

        var progress = await owner.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/watch-progress", new { watchedSeconds = 60, saveHistory = true });
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/reaction", new { type = "like" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v1/videos/{video.VideoId}/watch-progress", new { watchedSeconds = 30, saveHistory = true })).StatusCode);

        var history = await owner.GetFromJsonAsync<PageResult<WatchHistoryResponse>>("/api/v1/library/history?page=1&pageSize=50", JsonOptions);
        var liked = await owner.GetFromJsonAsync<PageResult<LibraryVideoResponse>>("/api/v1/library/liked?page=1&pageSize=50", JsonOptions);
        Assert.NotNull(history);
        Assert.NotNull(liked);
        Assert.Single(history.Items, item => item.VideoId == video.VideoId);
        Assert.Equal(30, Assert.Single(history.Items).WatchedSeconds);
        Assert.Single(liked.Items, item => item.VideoId == video.VideoId);

        var otherHistory = await other.GetFromJsonAsync<PageResult<WatchHistoryResponse>>("/api/v1/library/history?page=1&pageSize=50", JsonOptions);
        var otherLiked = await other.GetFromJsonAsync<PageResult<LibraryVideoResponse>>("/api/v1/library/liked?page=1&pageSize=50", JsonOptions);
        Assert.Empty(otherHistory!.Items);
        Assert.Empty(otherLiked!.Items);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/library/history/{video.VideoId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/library/history/{video.VideoId}")).StatusCode);
        history = await owner.GetFromJsonAsync<PageResult<WatchHistoryResponse>>("/api/v1/library/history?page=1&pageSize=50", JsonOptions);
        Assert.DoesNotContain(history!.Items, item => item.VideoId == video.VideoId);
        Assert.Single(liked.Items, item => item.VideoId == video.VideoId);
    }

    [Fact]
    public async Task VideoUpload_EmptyUnsupportedAndInvalidQualityFilesCreateNoVideo()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("video_invalid");
        var before = factory.Storage.Objects.Keys.ToHashSet();

        using (var emptyForm = new MultipartFormDataContent())
        {
            emptyForm.Add(new ByteArrayContent([]), "Video", "empty.mp4");
            var response = await owner.PostAsync("/api/v1/videos", emptyForm);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("VIDEO_REQUIRED", await response.Content.ReadAsStringAsync());
        }

        async Task<HttpResponseMessage> UploadInvalid(string contentType, string fileName, string quality = "720p")
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(channelId.ToString()), "ChannelId");
            form.Add(new StringContent("Invalid upload " + Guid.NewGuid().ToString("N")), "Title");
            form.Add(new StringContent("Invalid upload"), "Description");
            form.Add(new StringContent("120"), "Duration");
            form.Add(new StringContent(quality), "SourceQuality");
            var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes("not a supported media container"));
            bytes.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(bytes, "Video", fileName);
            return await owner.PostAsync("/api/v1/videos", form);
        }

        var invalidType = await UploadInvalid("text/plain", "not-video.txt");
        Assert.Equal(HttpStatusCode.BadRequest, invalidType.StatusCode);
        Assert.Contains("INVALID_VIDEO_TYPE", await invalidType.Content.ReadAsStringAsync());
        var invalidQuality = await UploadInvalid("video/mp4", "quality.mp4", "2160p");
        Assert.Equal(HttpStatusCode.Forbidden, invalidQuality.StatusCode);
        Assert.Contains("UPLOAD_QUALITY_DENIED", await invalidQuality.Content.ReadAsStringAsync());

        await using var db = factory.CreateDb();
        Assert.Empty(await db.Videos.Where(item => item.ChannelId == channelId && item.Title.StartsWith("Invalid upload")).ToListAsync());
        Assert.DoesNotContain(factory.Storage.Objects.Keys, key => !before.Contains(key) && key.Contains("Invalid upload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Playlist_SaveAndChannelListing_RespectVisibilityOwnershipAndDuplicateRules()
    {
        var (owner, ownerId, channelId) = await CreateUserAndChannelAsync("playlist_save_owner");
        var (other, _, otherChannelId) = await CreateUserAndChannelAsync("playlist_save_other");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Saved playlist video");
        var guest = factory.CreateClient();

        var firstSave = await owner.PostAsJsonAsync("/api/v1/playlists/save", new SaveVideoRequest(video.VideoId));
        Assert.Equal(HttpStatusCode.OK, firstSave.StatusCode);
        var saved = (await firstSave.Content.ReadFromJsonAsync<PlaylistResponse>(JsonOptions))!;
        Assert.Equal(ownerId, saved.UserId);
        Assert.Equal("Video đã lưu", saved.Name);
        Assert.Equal("private", saved.Visibility);
        Assert.Single(saved.Items, item => item.VideoId == video.VideoId);

        var duplicateSave = await owner.PostAsJsonAsync("/api/v1/playlists/save", new SaveVideoRequest(video.VideoId));
        Assert.Equal(HttpStatusCode.Conflict, duplicateSave.StatusCode);
        Assert.Contains("PLAYLIST_VIDEO_EXISTS", await duplicateSave.Content.ReadAsStringAsync());

        var publicPlaylistResponse = await owner.PostAsJsonAsync(
            "/api/v1/playlists", new CreatePlaylistRequest("Public channel list", null, "public"));
        var publicPlaylist = (await publicPlaylistResponse.Content.ReadFromJsonAsync<PlaylistResponse>(JsonOptions))!;
        (await owner.PostAsJsonAsync($"/api/v1/playlists/{publicPlaylist.PlaylistId}/videos",
            new AddPlaylistVideoRequest(video.VideoId))).EnsureSuccessStatusCode();

        var publicLists = await guest.GetFromJsonAsync<List<PlaylistSummaryResponse>>(
            $"/api/v1/playlists/channel/{channelId}", JsonOptions);
        Assert.NotNull(publicLists);
        Assert.Contains(publicLists!, item => item.PlaylistId == publicPlaylist.PlaylistId && item.Visibility == "public");
        Assert.DoesNotContain(publicLists!, item => item.PlaylistId == saved.PlaylistId);

        var ownerLists = await owner.GetFromJsonAsync<List<PlaylistSummaryResponse>>(
            $"/api/v1/playlists/channel/{channelId}/mine", JsonOptions);
        Assert.NotNull(ownerLists);
        Assert.Contains(ownerLists!, item => item.PlaylistId == saved.PlaylistId);
        Assert.Contains(ownerLists!, item => item.PlaylistId == publicPlaylist.PlaylistId);

        var wrongOwner = await other.GetAsync($"/api/v1/playlists/channel/{channelId}/mine");
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwner.StatusCode);
        var otherChannelLists = await guest.GetFromJsonAsync<List<PlaylistSummaryResponse>>(
            $"/api/v1/playlists/channel/{otherChannelId}", JsonOptions);
        Assert.NotNull(otherChannelLists);
        Assert.Empty(otherChannelLists!);
    }

    [Fact]
    public async Task Comments_RejectCrossVideoParentsAndSupportNestedShortReplies()
    {
        var (owner, _, ownerChannelId) = await CreateUserAndChannelAsync("comment_parent_owner");
        var (viewer, _, viewerChannelId) = await CreateUserAndChannelAsync("comment_parent_viewer");
        var firstVideo = await CreatePublishedVideoAsync(owner, ownerChannelId, "Comment parent video");
        var secondVideo = await CreatePublishedVideoAsync(viewer, viewerChannelId, "Other comment video");

        var parentResponse = await viewer.PostAsJsonAsync(
            $"/api/v1/videos/{firstVideo.VideoId}/comments",
            new { content = "Parent comment", parentCommentId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, parentResponse.StatusCode);
        var parent = (await parentResponse.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions))!;

        var crossVideo = await owner.PostAsJsonAsync(
            $"/api/v1/videos/{secondVideo.VideoId}/comments",
            new { content = "Cross video reply", parentCommentId = parent.CommentId });
        Assert.Equal(HttpStatusCode.NotFound, crossVideo.StatusCode);
        Assert.Contains("PARENT_COMMENT_NOT_FOUND", await crossVideo.Content.ReadAsStringAsync());

        var replyResponse = await owner.PostAsJsonAsync(
            $"/api/v1/videos/{firstVideo.VideoId}/comments",
            new { content = "First level reply", parentCommentId = parent.CommentId });
        Assert.Equal(HttpStatusCode.Created, replyResponse.StatusCode);
        var reply = (await replyResponse.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions))!;

        var nested = await viewer.PostAsJsonAsync(
            $"/api/v1/videos/{firstVideo.VideoId}/comments",
            new { content = "s", parentCommentId = reply.CommentId });
        Assert.Equal(HttpStatusCode.Created, nested.StatusCode);
        var child = (await nested.Content.ReadFromJsonAsync<CommentResponse>(JsonOptions))!;
        Assert.Equal(reply.CommentId, child.ParentCommentId);
        Assert.Equal("s", child.Content);
        for (var i = 0; i < 4; i++)
        {
            (await viewer.PostAsJsonAsync($"/api/v1/videos/{firstVideo.VideoId}/comments",
                new { content = $"Another nested reply {i}", parentCommentId = reply.CommentId })).EnsureSuccessStatusCode();
        }
        // Equal timestamps must still produce stable pages with no lost/duplicate replies.
        await using (var db = factory.CreateDb())
        {
            await db.Comments.Where(x => x.ParentCommentId == reply.CommentId)
                .ExecuteUpdateAsync(x => x.SetProperty(row => row.CreatedAt, DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
        }
        var guest = factory.CreateClient();
        var firstPage = await guest.GetFromJsonAsync<PageResult<CommentResponse>>(
            $"/api/v1/comments/{reply.CommentId}/replies?page=1&pageSize=3", JsonOptions);
        var secondPage = await guest.GetFromJsonAsync<PageResult<CommentResponse>>(
            $"/api/v1/comments/{reply.CommentId}/replies?page=2&pageSize=3", JsonOptions);
        Assert.Equal(5, firstPage!.Total);
        Assert.Equal(3, firstPage.Items.Count);
        Assert.Equal(2, secondPage!.Items.Count);
        Assert.Equal(5, firstPage.Items.Concat(secondPage.Items).Select(x => x.CommentId).Distinct().Count());
        var parentReplies = await guest.GetFromJsonAsync<PageResult<CommentResponse>>(
            $"/api/v1/comments/{parent.CommentId}/replies", JsonOptions);
        Assert.Equal(5, Assert.Single(parentReplies!.Items).ReplyCount);
        var empty = await viewer.PostAsJsonAsync($"/api/v1/videos/{firstVideo.VideoId}/comments",
            new { content = "  ", parentCommentId = reply.CommentId });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Comments_AllowTwoIdenticalPostsBeforeFlaggingTheThirdAsSpam()
    {
        var (owner, _, channelId) = await CreateUserAndChannelAsync("duplicate_comment_owner");
        var (viewer, _, _) = await CreateUserAndChannelAsync("duplicate_comment_viewer");
        var video = await CreatePublishedVideoAsync(owner, channelId, "Repeated comment threshold");
        var url = $"/api/v1/videos/{video.VideoId}/comments";
        var request = new { content = "Repeated comment", parentCommentId = (Guid?)null };

        var first = await viewer.PostAsJsonAsync(url, request);
        var second = await viewer.PostAsJsonAsync(url, request);
        var third = await viewer.PostAsJsonAsync(url, request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Contains("COMMENT_SPAM_DETECTED", await third.Content.ReadAsStringAsync());
    }

    private async Task WaitForReadyRenditionsAsync(Guid videoId, int expectedReadyCount)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var db = factory.CreateDb();
            var readyCount = await db.VideoRenditions.AsNoTracking()
                .CountAsync(x => x.VideoId == videoId && x.Status == "ready");
            if (readyCount >= expectedReadyCount) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Video {videoId} did not finish processing {expectedReadyCount} renditions within 10 seconds.");
    }

    private async Task ApproveAsync(Guid videoId)
    {
        await using var db = factory.CreateDb();
        await db.Videos.Where(x => x.VideoId == videoId).ExecuteUpdateAsync(x => x.SetProperty(v => v.ModerationStatus, "approved"));
        await db.ModerationCases.Where(x => x.VideoId == videoId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "approved").SetProperty(v => v.ResolvedAt, DateTimeOffset.UtcNow));
    }
}
