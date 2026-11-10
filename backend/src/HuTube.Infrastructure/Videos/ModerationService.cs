using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using HuTube.Application.Auth;
using HuTube.Application.Notifications;
using HuTube.Application.Rbac;
using HuTube.Application.Serialization;
using HuTube.Application.Storage;
using HuTube.Application.Videos;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Persistence;
using HuTube.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace HuTube.Infrastructure.Videos;

public sealed class ModerationService(
    HuTubeDbContext db,
    RbacService rbac,
    IObjectStorage storage,
    StrikePolicySettingsService strikePolicySettings,
    ILogger<ModerationService>? logger = null,
    BackgroundWorkWakeup<ModerationOutboxWorker>? outboxWakeup = null)
{
    public ModerationService(HuTubeDbContext db, RbacService rbac, IObjectStorage storage)
        : this(db, rbac, storage, new StrikePolicySettingsService(db, rbac)) { }

    public async Task<List<ModerationQueueItemResponse>> GetQueueAsync(string? status = null, string? riskLevel = null, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.ModerationCases.AsNoTracking().Where(c => c.CaseType == "upload_review");

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim().ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            query = normalizedStatus == "pending"
                ? query.Where(c => c.Status == "pending" || c.Status == "reviewing" && (c.ClaimId == null || c.ClaimExpiresAt == null || c.ClaimExpiresAt <= now))
                : normalizedStatus == "reviewing"
                ? query.Where(c => c.Status == "reviewing" && c.ClaimId != null && c.ClaimExpiresAt > now)
                : normalizedStatus == "processed"
                ? query.Where(c => c.Status == "approved" || c.Status == "rejected")
                : query.Where(c => c.Status == normalizedStatus);
        }
        else
        {
            query = query.Where(c => c.Status == "pending" || c.Status == "reviewing" || c.Status == "escalated");
        }

        if (!string.IsNullOrWhiteSpace(riskLevel))
        {
            var normalizedRisk = riskLevel.Trim().ToLowerInvariant();
            query = normalizedRisk == "high" ? query.Where(c => c.RiskLevel == "high") : query.Where(c => c.RiskLevel != "high");
        }

        var sorted = query.OrderBy(c => c.RiskLevel == "high" ? 0 : 1)
                          .ThenBy(c => c.SubmittedAt);

        var cases = await sorted.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        if (cases.Count == 0) return [];

        var videoIds = cases.Where(c => c.VideoId.HasValue).Select(c => c.VideoId!.Value).Distinct().ToList();
        var videos = await db.Videos.AsNoTracking().Where(v => videoIds.Contains(v.VideoId)).ToDictionaryAsync(v => v.VideoId, ct);

        var channelIds = videos.Values.Select(v => v.ChannelId).Distinct().ToList();
        var channels = await db.Channels.AsNoTracking().Where(c => channelIds.Contains(c.ChannelId)).ToDictionaryAsync(c => c.ChannelId, ct);
        var categoryIds = videos.Values.Where(v => v.CategoryId.HasValue).Select(v => v.CategoryId!.Value).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.CategoryId)).ToDictionaryAsync(c => c.CategoryId, ct);

        var reviewerIds = cases.Where(c => c.ReviewerId.HasValue).Select(c => c.ReviewerId!.Value).Distinct().ToList();
        var reviewers = await db.Users.AsNoTracking().Where(u => reviewerIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, ct);

        var result = new List<ModerationQueueItemResponse>();
        foreach (var c in cases)
        {
            if (!c.VideoId.HasValue || !videos.TryGetValue(c.VideoId.Value, out var video)) continue;
            channels.TryGetValue(video.ChannelId, out var channel);
            categories.TryGetValue(video.CategoryId ?? Guid.Empty, out var category);
            reviewers.TryGetValue(c.ReviewerId ?? Guid.Empty, out var reviewer);
            var videoUrl = await ReadUrlAsync(video.VideoUrl, ct);
            var thumbnailUrl = string.IsNullOrWhiteSpace(video.ThumbnailUrl)
                ? null
                : await ReadUrlAsync(video.ThumbnailUrl, ct);

            result.Add(new ModerationQueueItemResponse(
                c.ModerationCaseId,
                video.VideoId,
                video.Title,
                video.Description,
                videoUrl,
                thumbnailUrl,
                video.Duration,
                video.ChannelId,
                channel?.Name ?? "Kênh không xác định",
                channel?.Handle,
                channel?.AvatarUrl,
                category?.Name,
                c.CaseType,
                c.Status,
                c.RiskLevel == "high" ? "high" : "low",
                c.ReviewerId,
                reviewer?.DisplayName,
                c.SubmittedAt,
                c.ClaimedAt,
                c.Note, c.ClaimId, c.ClaimExpiresAt
            ));
        }

        return result;
    }

    public async Task<ClaimLeaseResponse> ClaimCaseAsync(Guid actorId, Guid caseId, CancellationToken ct = default)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var item = await db.ModerationCases.FirstOrDefaultAsync(x => x.ModerationCaseId == caseId, ct)
            ?? throw new AuthException(404, "CASE_NOT_FOUND", "Không tìm thấy hồ sơ.");
        await ClaimLeasePolicy.LockAsync(db, item, ct);
        var previous = item.ClaimId;
        ClaimLeasePolicy.Assign(item, actorId, DateTimeOffset.UtcNow);
        if (previous != item.ClaimId)
        {
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await rbac.LogAuditAsync(new AuditLogEntry(actorId, "moderation.claim", "moderation", caseId, "Nhận hồ sơ trong 15 phút"), ct);
        }
        if (transaction != null) await transaction.CommitAsync(ct);
        return new(item.ClaimId, item.ClaimExpiresAt);
    }

    public async Task<bool> ReleaseCaseAsync(Guid actorId, Guid caseId, CancellationToken ct = default, Guid? claimId = null)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var item = await db.ModerationCases.FirstOrDefaultAsync(x => x.ModerationCaseId == caseId, ct)
            ?? throw new AuthException(404, "CASE_NOT_FOUND", "Không tìm thấy hồ sơ.");
        await ClaimLeasePolicy.LockAsync(db, item, ct);
        ClaimLeasePolicy.Ensure(item, actorId, claimId, DateTimeOffset.UtcNow);
        ClaimLeasePolicy.Release(item);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await rbac.LogAuditAsync(new AuditLogEntry(actorId, "moderation.release", "moderation", caseId, "Trả hồ sơ về hàng đợi"), ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<ModerationDecisionResponse> ResolveCaseAsync(Guid actorId, Guid caseId, ResolveModerationRequest request, CancellationToken ct = default)
    {
        var elapsed = Stopwatch.StartNew();
        var pendingNotifications = new List<NotificationMessage>();
        void QueueNotification(Guid userId, string type, string title, string content, string? actionUrl,
            string? resourceType, Guid? resourceId, CancellationToken cancellation) =>
            pendingNotifications.Add(new(userId, type, title, content, actionUrl, resourceType, resourceId));
        var decision = request.Decision.Trim().ToLowerInvariant();
        if (decision is not ("approve" or "age_restricted" or "recommendation_restricted" or "reject" or "escalate"))
            throw new AuthException(400, "INVALID_DECISION", "Quyết định không hợp lệ. Chỉ chấp nhận approve, age_restricted, recommendation_restricted, reject, escalate.");

        // Serialize decisions for the same case before any audit/notification
        // side effects. A second request observes the committed terminal state.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var cases = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
            ? db.ModerationCases.FromSqlInterpolated($"SELECT * FROM moderation_cases WHERE moderation_case_id = {caseId} FOR UPDATE")
            : db.ModerationCases;
        var moderationCase = await cases.FirstOrDefaultAsync(c => c.ModerationCaseId == caseId, ct)
            ?? throw new AuthException(404, "CASE_NOT_FOUND", "Không tìm thấy hồ sơ kiểm duyệt.");

        if (moderationCase.ResolvedAt.HasValue || moderationCase.Status is "approved" or "rejected" or "resolved")
            throw new AuthException(409, "CASE_ALREADY_PROCESSED", "Hồ sơ kiểm duyệt đã được xử lý.");

        ClaimLeasePolicy.Ensure(moderationCase, actorId, request.ClaimId, DateTimeOffset.UtcNow);

        if (!moderationCase.VideoId.HasValue)
            throw new AuthException(400, "INVALID_CASE_TARGET", "Hồ sơ kiểm duyệt không gắn với video nào.");

        var video = await db.Videos.FirstOrDefaultAsync(v => v.VideoId == moderationCase.VideoId.Value, ct)
            ?? throw new AuthException(404, "VIDEO_NOT_FOUND", "Không tìm thấy video cần duyệt.");

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChannelId == video.ChannelId, ct)
            ?? throw new AuthException(404, "CHANNEL_NOT_FOUND", "Kênh của video không còn khả dụng.");
        if (channel.Status != "active")
            throw new AuthException(409, "CHANNEL_NOT_ACTIVE", "Không thể quyết định video khi kênh đang bị hạn chế.");
        var now = DateTimeOffset.UtcNow;
        var message = "";

        moderationCase.ReviewerId = actorId;
        moderationCase.Decision = decision;
        moderationCase.PolicyCode = string.IsNullOrWhiteSpace(request.PolicyCode)
            ? null
            : request.PolicyCode.Trim();
        // Capture the published policy at decision time. Later publications
        // must never change the policy version attached to this decision.
        moderationCase.PolicyVersion = moderationCase.PolicyCode == null ? null
            : await db.Policies.AsNoTracking()
                .Where(p => p.Code == moderationCase.PolicyCode && p.Status == "published" && p.EffectiveAt <= now)
                .OrderByDescending(p => p.EffectiveAt).ThenByDescending(p => p.CreatedAt)
                .Select(p => p.Version).FirstOrDefaultAsync(ct);
        moderationCase.InternalNote = request.InternalNote?.Trim();
        moderationCase.UpdatedAt = now;
        var wasPublished = video.Status == "published" && video.PublishedAt.HasValue;

        switch (decision)
        {
            case "approve":
                video.ModerationStatus = "approved";
                video.Status = "published";
                video.PublishedAt ??= now;
                video.UpdatedAt = now;

                moderationCase.Status = "approved";
                moderationCase.ResolvedAt = now;
                message = "Đã phê duyệt video an toàn và xuất bản thành công.";

                if (channel != null)
                {
                    QueueNotification(channel.OwnerUserId, "video_approved", "Video đã được phê duyệt",
                        $"Video '{video.Title}' đã được phê duyệt và xuất bản công khai.", $"/watch/{video.VideoId}", "video", video.VideoId, ct);
                }
                break;

            case "age_restricted":
                video.ModerationStatus = "approved";
                video.AgeRestricted = true;
                video.Status = "published";
                video.PublishedAt ??= now;
                video.UpdatedAt = now;

                moderationCase.Status = "approved";
                moderationCase.ResolvedAt = now;
                message = "Đã phê duyệt video với giới hạn độ tuổi (18+).";

                if (channel != null)
                {
                    QueueNotification(channel.OwnerUserId, "video_age_restricted", "Video gắn giới hạn độ tuổi",
                        $"Video '{video.Title}' được duyệt nhưng bị gắn nhãn 18+ do chứa nội dung phù hợp người trưởng thành.", $"/watch/{video.VideoId}", "video", video.VideoId, ct);
                }
                break;

            case "recommendation_restricted":
                video.ModerationStatus = "approved";
                video.Status = "published";
                video.PublishedAt ??= now;
                video.UpdatedAt = now;

                try
                {
                    var meta = JsonSerializer.Deserialize<Dictionary<string, object>>(video.Metadata) ?? [];
                    meta["recommendation_restricted"] = true;
                    video.Metadata = PersistenceJson.Serialize(meta);
                }
                catch
                {
                    video.Metadata = "{\"recommendation_restricted\":true}";
                }

                moderationCase.Status = "approved";
                moderationCase.ResolvedAt = now;
                message = "Đã phê duyệt video với hạn chế đề xuất.";

                if (channel != null)
                {
                    QueueNotification(channel.OwnerUserId, "video_restricted", "Video hạn chế phân phối",
                        $"Video '{video.Title}' đã xuất bản nhưng bị hạn chế gợi ý tại Trang chủ/Khám phá do nội dung nhạy cảm.", $"/watch/{video.VideoId}", "video", video.VideoId, ct);
                }
                break;

            case "reject":
                if (string.IsNullOrWhiteSpace(request.PolicyCode) || string.IsNullOrWhiteSpace(request.Reason))
                    throw new AuthException(400, "REJECTION_DETAILS_REQUIRED", "Bắt buộc chọn mã chính sách và điền lý do từ chối.");

                video.ModerationStatus = "rejected";
                video.ModerationReason = request.Reason.Trim();
                video.MediaRetentionUntil ??= now.AddDays(30);
                // The videos.status constraint uses "blocked" for content
                // that cannot be published; "rejected" belongs to the
                // moderation status field.
                video.Status = "blocked";
                video.UpdatedAt = now;

                moderationCase.Status = "rejected";
                moderationCase.Note = request.Reason.Trim();
                moderationCase.ResolvedAt = now;
                message = $"Đã từ chối xuất bản video do vi phạm chính sách {request.PolicyCode}.";

                if (channel != null)
                {
                    QueueNotification(channel.OwnerUserId, "video_rejected", "Video bị từ chối xuất bản",
                        $"Video '{video.Title}' bị từ chối xuất bản do vi phạm điều khoản {request.PolicyCode}. Lý do: {request.Reason}", $"/studio", "video", video.VideoId, ct);

                    var strikePolicy = await strikePolicySettings.GetAsync(ct);
                    var rejectedVideoIds = await db.Videos.AsNoTracking()
                        .Where(v => v.ChannelId == channel.ChannelId && v.ModerationStatus == "rejected"
                            && v.UpdatedAt >= strikePolicy.RejectedVideosEffectiveAt)
                        .Select(v => v.VideoId)
                        .Distinct()
                        .ToListAsync(ct);
                    if (!rejectedVideoIds.Contains(video.VideoId)) rejectedVideoIds.Add(video.VideoId);
                    var distinctRejectedCount = rejectedVideoIds.Count;

                    var automaticStrikesIssued = await db.ChannelStrikes.AsNoTracking()
                        .CountAsync(s => s.ChannelId == channel.ChannelId && s.PolicyCode == "SPAM.REPEATED_VIOLATIONS"
                            && s.SourceModerationCaseId.HasValue && s.CreatedAt >= strikePolicy.RejectedVideosEffectiveAt
                            && s.Status != StrikeStatuses.Revoked, ct);
                    var automaticStrikesEarned = distinctRejectedCount / strikePolicy.RejectedVideosPerStrike;

                    if (automaticStrikesEarned > automaticStrikesIssued && channel.Status != "suspended")
                    {
                        var activeStrikeCount = await db.ChannelStrikes.AsNoTracking()
                            .CountAsync(s => s.ChannelId == channel.ChannelId && s.StrikeNumber > 0
                                && s.Status == StrikeStatuses.Active && s.ExpiresAt > now, ct);
                        var nextStrikeNumber = activeStrikeCount + 1;
                        var shouldSuspendChannel = nextStrikeNumber >= strikePolicy.SuspensionStrikeCount;
                        var restrictionDays = StrikePolicySettingsService.RestrictionDaysFor(nextStrikeNumber, strikePolicy);
                        var strikeReason = $"Kênh có {distinctRejectedCount} video bị từ chối kiểm duyệt; cứ {strikePolicy.RejectedVideosPerStrike} video bị từ chối sẽ tính một gậy.";
                        var strike = new ChannelStrike
                        {
                            ChannelId = channel.ChannelId,
                            UserId = channel.OwnerUserId,
                            StrikeNumber = nextStrikeNumber,
                            Severity = StrikeSeverities.High,
                            PolicyCode = "SPAM.REPEATED_VIOLATIONS",
                            Reason = strikeReason,
                            InternalNote = "Gậy được cấp tự động theo quy tắc từ chối video lặp lại.",
                            Status = StrikeStatuses.Active,
                            ExpiresAt = now.AddDays(strikePolicy.StrikeExpirationDays),
                            UploadRestrictedUntil = now.AddDays(restrictionDays),
                            CreatedAt = now,
                            SourceModerationCaseId = moderationCase.ModerationCaseId
                        };
                        db.ChannelStrikes.Add(strike);

                        var strikeNotification = shouldSuspendChannel
                            ? $"{strikeReason} Kênh đạt ngưỡng {strikePolicy.SuspensionStrikeCount} gậy đang hiệu lực và đã bị khóa."
                            : $"{strikeReason} Gậy này hạn chế quyền đăng tải video trong {restrictionDays} ngày.";
                        QueueNotification(
                            channel.OwnerUserId,
                            "channel_strike",
                            $"Kênh nhận Gậy phạt {nextStrikeNumber}",
                            strikeNotification,
                            "/studio",
                            "channel",
                            channel.ChannelId,
                            ct);

                        if (shouldSuspendChannel)
                        {
                            channel.Status = "suspended";
                            channel.SuspendedUntil = null;
                            db.Entry(channel).Property(c => c.Status).IsModified = true;
                            db.Entry(channel).Property(c => c.SuspendedUntil).IsModified = true;
                            channel.StatusReason = $"Kênh đạt {nextStrikeNumber} gậy đang hiệu lực.";
                            channel.UpdatedAt = now;

                            await rbac.LogAuditAsync(new AuditLogEntry(
                                actorId,
                                "channel.auto_lock",
                                "channel",
                                channel.ChannelId,
                                $"Tự động khóa kênh '{channel.Name}' do đạt {nextStrikeNumber} gậy đang hiệu lực."
                            ), ct);

                            QueueNotification(
                                channel.OwnerUserId,
                                "channel_suspended",
                                "Kênh của bạn đã bị khóa tự động",
                                $"Kênh '{channel.Name}' đã bị khóa tự động do đạt {nextStrikeNumber} gậy đang hiệu lực. Vui lòng gửi khiếu nại nếu bạn cho rằng đây là nhầm lẫn.",
                                "/studio",
                                "channel",
                                channel.ChannelId,
                                ct
                            );
                        }
                    }
                }
                break;

            case "escalate":
                moderationCase.Status = "escalated";
                message = "Đã chuyển hồ sơ lên cấp quản trị cao hơn.";
                break;
        }

        var notificationEvent = new VideoModerationEvent(Guid.NewGuid(), caseId, video.VideoId, channel!.ChannelId,
            channel.Name, video.Title, decision,
            !wasPublished && (decision is "approve" or "age_restricted" or "recommendation_restricted") && video.Visibility == "public",
            now, pendingNotifications);
        db.ModerationOutboxEvents.Add(new ModerationOutboxEvent {
            EventId = notificationEvent.EventId, EventType = notificationEvent.EventType,
            Payload = PersistenceJson.Serialize(notificationEvent), OccurredAt = now, AvailableAt = now
        });
        await db.SaveChangesAsync(ct);

        await rbac.LogAuditAsync(new AuditLogEntry(
            actorId,
            $"moderation.{decision}",
            "video",
            video.VideoId,
            $"{message} - Lý do: {request.Reason ?? "Không có"} - Policy: {request.PolicyCode ?? "N/A"}"
        ), ct);

        if (transaction != null) await transaction.CommitAsync(ct);
        outboxWakeup?.Signal();
        logger?.LogInformation("Moderation decision and outbox {EventId} committed in {ElapsedMs} ms", notificationEvent.EventId, elapsed.ElapsedMilliseconds);
        return new ModerationDecisionResponse(
            moderationCase.ModerationCaseId,
            video.VideoId,
            moderationCase.Status,
            decision,
            moderationCase.PolicyCode,
            message
        );
    }

    private Task<string> ReadUrlAsync(string path, CancellationToken ct) =>
        storage.GetReadUrlAsync(path, TimeSpan.FromMinutes(60), ct);
}
