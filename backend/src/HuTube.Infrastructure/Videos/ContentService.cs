using System.Data;
using System.Net.Http;
using System.Text.Json;
using HuTube.Application.Recommendations;
using HuTube.Application.Serialization;
using HuTube.Application.Storage;
using HuTube.Application.Notifications;
using HuTube.Application.Videos;
using HuTube.Domain.Channels;
using HuTube.Domain.Plans;
using HuTube.Domain.Rbac;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Videos;

public sealed class ContentService(
    HuTubeDbContext db,
    IObjectStorage storage,
    IVideoTranscoder transcoder,
    INotificationService notifications,
    FeatureOptions features,
    TimeProvider clock,
    ILogger<ContentService> logger,
    IHttpClientFactory httpClientFactory,
    VideoRenditionProcessingQueue renditionQueue,
    IRecommendationClient recommendationClient,
    StrikePolicySettingsService strikePolicySettings) : IContentService
{
    private static readonly string VideoPromotionFeatureJson =
        $"{{\"{PlanEntitlementRules.VideoPromotion}\":true}}";
    private sealed class FeedVideoRow
    {
        public Guid VideoId { get; init; }
        public Guid ChannelId { get; init; }
        public string ChannelName { get; init; } = "";
        public string ChannelHandle { get; init; } = "";
        public string Title { get; init; } = "";
        public string? ThumbnailUrl { get; init; }
        public int Duration { get; init; }
        public string Visibility { get; init; } = "public";
        public DateTimeOffset? PublishedAt { get; init; }
        public long? Views { get; init; }
        public bool IsPromoted { get; init; }
    }
    private sealed record ViewerHistoryRow(int WatchDuration, decimal Progress);
    private sealed class ExploreCategoryRow
    {
        public Guid VideoId { get; init; }
        public Guid ChannelId { get; init; }
        public string ChannelName { get; init; } = "";
        public string ChannelHandle { get; init; } = "";
        public Guid? CategoryId { get; init; }
        public string Title { get; init; } = "";
        public string? ThumbnailUrl { get; init; }
        public int Duration { get; init; }
        public string Visibility { get; init; } = "public";
        public DateTimeOffset? PublishedAt { get; init; }
        public long Views { get; init; }
        public int CategoryRank { get; init; }
        public int GlobalRank { get; init; }
    }
    private sealed class FeaturedChannelRow
    {
        public Guid ChannelId { get; init; }
        public string Name { get; init; } = "";
        public string Handle { get; init; } = "";
        public string? AvatarUrl { get; init; }
        public long SubscriberCount { get; init; }
    }
    private static readonly TimeSpan ViewSessionCooldown = TimeSpan.FromMinutes(30);
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<ExploreHubResponse> GetExploreHubAsync(CancellationToken ct = default)
    {
        // Keep both category top-3 and global top-12 in one SQL window query.
        // PostgreSQL executes ROW_NUMBER() OVER (PARTITION BY category_id ...)
        // server-side; EF Core cannot translate the equivalent LINQ shape.
        var exploreRows = await db.Database.SqlQuery<ExploreCategoryRow>($"""
            WITH view_counts AS (
                SELECT video_id, COUNT(*)::bigint AS views
                FROM public.viewing_histories
                GROUP BY video_id
            ), public_rows AS (
                SELECT
                    video.video_id AS "VideoId",
                    video.channel_id AS "ChannelId",
                    channel.name AS "ChannelName",
                    channel.handle AS "ChannelHandle",
                    video.category_id AS "CategoryId",
                    video.title AS "Title",
                    video.thumbnail_url AS "ThumbnailUrl",
                    video.duration AS "Duration",
                    video.visibility AS "Visibility",
                    video.published_at AS "PublishedAt",
                    COALESCE(view_counts.views, 0)::bigint AS "Views"
                FROM public.videos AS video
                INNER JOIN public.channels AS channel ON channel.channel_id = video.channel_id
                LEFT JOIN view_counts ON view_counts.video_id = video.video_id
                WHERE video.status = 'published'
                  AND video.moderation_status = 'approved'
                  AND video.visibility = 'public'
                  AND channel.status = 'active'
                  AND NOT video.moderation_hidden AND NOT video.recommendation_restricted
                  AND video.media_purged_at IS NULL
            ), ranked AS (
                SELECT public_rows.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY "CategoryId"
                           ORDER BY "Views" DESC, "PublishedAt" DESC NULLS LAST, "VideoId"
                       )::int AS "CategoryRank",
                       ROW_NUMBER() OVER (
                           ORDER BY "Views" DESC, "PublishedAt" DESC NULLS LAST, "VideoId"
                       )::int AS "GlobalRank"
                FROM public_rows
            )
            SELECT "VideoId", "ChannelId", "ChannelName", "ChannelHandle", "CategoryId",
                   "Title", "ThumbnailUrl", "Duration", "Visibility", "PublishedAt", "Views",
                   "CategoryRank", "GlobalRank"
            FROM ranked
            WHERE ("CategoryId" IS NOT NULL AND "CategoryRank" <= 3) OR "GlobalRank" <= 12
            """).ToListAsync(ct);
        var categoryRows = exploreRows.Where(row => row.CategoryId.HasValue && row.CategoryRank <= 3).ToList();
        var exploreUrls = await ResolveReadUrlsAsync(categoryRows.Select(row => row.ThumbnailUrl), ct);
        var categoryIds = categoryRows.Select(row => row.CategoryId!.Value).Distinct().ToArray();
        var categories = await db.Categories.AsNoTracking()
            .Where(category => categoryIds.Contains(category.CategoryId) && category.Status == "active")
            .ToDictionaryAsync(category => category.CategoryId, ct);
        var rankings = categoryRows
            .Where(row => categories.ContainsKey(row.CategoryId!.Value))
            .GroupBy(row => row.CategoryId!.Value)
            .OrderBy(group => categories[group.Key].Name)
            .Take(8)
            .Select(group => new CategoryRankingGroup(group.Key, categories[group.Key].Name, categories[group.Key].Slug,
                group.Select(row => new VideoCardResponse(row.VideoId, row.ChannelId, row.ChannelName, row.ChannelHandle,
                    row.Title, row.ThumbnailUrl == null ? null : exploreUrls.GetValueOrDefault(row.ThumbnailUrl),
                    row.Duration, row.Visibility, row.PublishedAt, row.Views)).ToList()))
            .ToList();

        // Use one global top-12 result for both the ranking block and trending.
        var topRows = exploreRows.Where(row => row.GlobalRank <= 12)
            .OrderBy(row => row.GlobalRank).ToList();
        foreach (var path in topRows.Select(row => row.ThumbnailUrl).Where(path => path != null && !exploreUrls.ContainsKey(path!)))
            exploreUrls[path!] = await ReadUrlAsync(path!, ct);
        var topCards = topRows.Select(row => new VideoCardResponse(row.VideoId, row.ChannelId, row.ChannelName,
            row.ChannelHandle, row.Title, row.ThumbnailUrl == null ? null : exploreUrls.GetValueOrDefault(row.ThumbnailUrl),
            row.Duration, row.Visibility, row.PublishedAt, row.Views)).ToList();

        // Featured creators use the same pre-aggregated counts without relying on
        // an EF left-join null conditional in ORDER BY/WHERE (unsupported by the
        // provider version used by this project).
        var topChannels = await db.Database.SqlQuery<FeaturedChannelRow>($"""
            WITH subscriber_counts AS (
                SELECT channel_id, COUNT(*)::bigint AS count
                FROM public.subscriptions
                WHERE status = 'active'
                GROUP BY channel_id
            ), video_counts AS (
                SELECT channel_id, COUNT(*)::bigint AS count
                FROM public.videos
                WHERE status = 'published' AND moderation_status = 'approved'
                GROUP BY channel_id
            )
            SELECT channel.channel_id AS "ChannelId",
                   channel.name AS "Name",
                   channel.handle AS "Handle",
                   channel.avatar_url AS "AvatarUrl",
                   COALESCE(subscriber_counts.count, 0)::bigint AS "SubscriberCount"
            FROM public.channels AS channel
            LEFT JOIN subscriber_counts ON subscriber_counts.channel_id = channel.channel_id
            LEFT JOIN video_counts ON video_counts.channel_id = channel.channel_id
            WHERE channel.status = 'active'
              AND (COALESCE(subscriber_counts.count, 0) > 0 OR COALESCE(video_counts.count, 0) > 0)
            ORDER BY COALESCE(subscriber_counts.count, 0) DESC,
                     COALESCE(video_counts.count, 0) DESC,
                     channel.created_at DESC
            LIMIT 5
            """).ToListAsync(ct);

        var creators = new List<FeaturedCreatorResponse>();
        foreach (var ch in topChannels)
        {
            var avatar = ch.AvatarUrl == null ? null : await ReadUrlAsync(ch.AvatarUrl, ct);
            creators.Add(new(ch.ChannelId, ch.Name, ch.Handle, avatar, ch.SubscriberCount, true));
        }

        var trendingCards = topCards.Take(10).ToList();
        return new ExploreHubResponse(rankings, creators, trendingCards, topCards);
    }

    public async Task<IReadOnlyList<CategoryResponse>> GetCategoriesAsync(CancellationToken ct = default) =>
        await db.Categories.AsNoTracking().Where(x => x.Status == "active").OrderBy(x => x.Name)
            .Select(x => new CategoryResponse(x.CategoryId, x.Name, x.Slug, x.Description)).ToListAsync(ct);

    public Task<PageResult<VideoCardResponse>> GetFeedAsync(
        string feed,
        string? sort,
        Guid? categoryId,
        string? tag,
        int page,
        int pageSize,
        Guid? currentUserId = null,
        CancellationToken ct = default) =>
        GetFeedCoreAsync(feed, sort, categoryId, tag, page, pageSize, currentUserId, null, ct);

    public async Task<RecommendationContextResponse> GetRecommendationContextAsync(Guid userId, CancellationToken ct = default) =>
        new(userId, recommendationClient.IsEnabled, await GetRecentlyViewedVideoIdsAsync(userId, ct));

    public Task<PageResult<VideoCardResponse>> ResolveFeedAsync(
        Guid userId, IReadOnlyList<Guid> videoIds, int page, int pageSize, CancellationToken ct = default) =>
        GetFeedCoreAsync("home", "recommended", null, null, page, pageSize, userId, videoIds, ct);

    private Task<List<Guid>> GetRecentlyViewedVideoIdsAsync(Guid userId, CancellationToken ct) =>
        db.ViewingHistories.AsNoTracking().Where(x => x.UserId == userId)
            .GroupBy(x => x.VideoId)
            .Select(group => new { VideoId = group.Key, LastViewedAt = group.Max(x => x.ViewedAt) })
            .OrderByDescending(x => x.LastViewedAt).Take(5000)
            .Select(x => x.VideoId).ToListAsync(ct);

    private async Task<PageResult<VideoCardResponse>> GetFeedCoreAsync(
        string feed, string? sort, Guid? categoryId, string? tag, int page, int pageSize,
        Guid? currentUserId, IReadOnlyList<Guid>? recommendedVideoIds, CancellationToken ct)
    {
        (page, pageSize) = Page(page, pageSize);
        // Read the current viewing history for every home/recommended page. A model snapshot
        // cannot know about views recorded after it was trained.
        var hideViewed = currentUserId.HasValue && string.Equals(feed, "home", StringComparison.OrdinalIgnoreCase);
        var viewedIds = hideViewed
            ? await GetRecentlyViewedVideoIdsAsync(currentUserId!.Value, ct)
            : [];

        var isHomeRecommendationFeed = string.Equals(feed, "home", StringComparison.OrdinalIgnoreCase)
            && !categoryId.HasValue && string.IsNullOrWhiteSpace(tag);
        IQueryable<Video>? promotedVideosQuery = null;
        var promotedVideoCount = 0;
        if (isHomeRecommendationFeed)
        {
            var now = clock.GetUtcNow();
            promotedVideosQuery = VideoAccessPolicy.Catalogue(db).Where(video =>
                video.PromotionEnabled
                && video.PublishedAt.HasValue && db.PlanHistories.AsNoTracking().Any(history =>
                    history.Status == "active"
                    && history.StartedAt <= now
                    && (!history.EndedAt.HasValue || history.EndedAt > now)
                    && (history.UserId == video.UploadedByUserId
                        || db.PlanMembers.AsNoTracking().Any(member =>
                            member.PlanHistoryId == history.PlanHistoryId
                            && member.MemberUserId == video.UploadedByUserId
                            && member.Status == "accepted"
                            && member.AcceptedAt <= now))
                    && db.Plans.AsNoTracking().Any(plan => plan.PlanId == history.PlanId
                        && (plan.Status == "active" || plan.Status == "archived")
                        && EF.Functions.JsonContains(plan.Features, VideoPromotionFeatureJson))));
            if (hideViewed)
                promotedVideosQuery = promotedVideosQuery.Where(video =>
                    !db.ViewingHistories.Any(history => history.UserId == currentUserId!.Value && history.VideoId == video.VideoId));
            promotedVideoCount = await promotedVideosQuery.CountAsync(ct);
        }

        IQueryable<FeedVideoRow> FeedRows(IQueryable<Video> videos, bool isPromoted)
        {
            var viewCounts = db.ViewingHistories.AsNoTracking().GroupBy(history => history.VideoId)
                .Select(group => new { VideoId = group.Key, Count = group.LongCount() });
            return from video in videos
                   join channel in db.Channels.AsNoTracking() on video.ChannelId equals channel.ChannelId
                   join views in viewCounts on video.VideoId equals views.VideoId into viewRows
                   from views in viewRows.DefaultIfEmpty()
                   select new FeedVideoRow
                   {
                       VideoId = video.VideoId,
                       ChannelId = video.ChannelId,
                       ChannelName = channel.Name,
                       ChannelHandle = channel.Handle,
                       Title = video.Title,
                       ThumbnailUrl = video.ThumbnailUrl,
                       Duration = video.Duration,
                       Visibility = video.Visibility,
                       PublishedAt = video.PublishedAt,
                       Views = (long?)views!.Count,
                       IsPromoted = isPromoted
                   };
        }

        async Task<List<VideoCardResponse>> ToFeedCardsAsync(IEnumerable<FeedVideoRow> rows)
        {
            var cards = new List<VideoCardResponse>();
            foreach (var row in rows)
                cards.Add(new(row.VideoId, row.ChannelId, row.ChannelName, row.ChannelHandle, row.Title,
                    row.ThumbnailUrl == null ? null : await ReadUrlAsync(row.ThumbnailUrl, ct), row.Duration,
                    row.Visibility, row.PublishedAt, row.Views ?? 0L, row.IsPromoted));
            return cards;
        }

        // Use the model across every home/recommended page, with live exclusions.
        if (string.Equals(feed, "home", StringComparison.OrdinalIgnoreCase)
            && currentUserId.HasValue
            && !categoryId.HasValue
            && string.IsNullOrWhiteSpace(tag)
            && (recommendedVideoIds is not null || recommendationClient.IsEnabled))
        {
            try
            {
                var recommended = recommendedVideoIds ??
                    (await recommendationClient.GetRecommendationsAsync(currentUserId.Value, 100, viewedIds, ct))?
                    .Select(item => item.VideoId).ToList();
                if ((recommended?.Count ?? 0) > 0 || promotedVideoCount > 0)
                {
                    var candidateRecIds = recommended?.Where(id => !viewedIds.Contains(id)).Distinct().ToList() ?? [];
                    var promotedRecIds = promotedVideoCount > 0 && candidateRecIds.Count > 0
                        ? await promotedVideosQuery!.Where(video => candidateRecIds.Contains(video.VideoId))
                            .Select(video => video.VideoId).ToListAsync(ct)
                        : [];
                    var promotedRecIdSet = promotedRecIds.ToHashSet();
                    var recIds = candidateRecIds.Where(id => !promotedRecIdSet.Contains(id)).ToList();
                    var recVideos = await FeedRows(VideoAccessPolicy.Catalogue(db)
                            .Where(video => recIds.Contains(video.VideoId)
                                && !db.ViewingHistories.Any(history => history.UserId == currentUserId.Value && history.VideoId == video.VideoId)),
                            isPromoted: false)
                        .ToListAsync(ct);

                    // Keep the collaborative-filter order after the paid placements.
                    var rowsById = recVideos.ToDictionary(row => row.VideoId);
                    var orderedModelRows = recIds.Where(rowsById.ContainsKey).Select(id => rowsById[id]).ToList();
                    var pageOffset = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
                    var personalizedPageRows = new List<FeedVideoRow>(pageSize);
                    if (promotedVideoCount > pageOffset)
                    {
                        var promotionRows = await FeedRows(promotedVideosQuery!, isPromoted: true)
                            .OrderByDescending(row => row.PublishedAt).ThenBy(row => row.VideoId)
                            .Skip(pageOffset).Take(pageSize).ToListAsync(ct);
                        personalizedPageRows.AddRange(promotionRows);
                    }

                    var organicOffset = Math.Max(0, pageOffset - promotedVideoCount);
                    var modelRowsForPage = orderedModelRows.Skip(organicOffset).Take(pageSize - personalizedPageRows.Count).ToList();
                    personalizedPageRows.AddRange(modelRowsForPage);

                    if (personalizedPageRows.Count < pageSize)
                    {
                        var fallbackSkip = Math.Max(0, organicOffset - orderedModelRows.Count);
                        var fallbackVideosQuery = VideoAccessPolicy.Catalogue(db)
                            .Where(video => !db.ViewingHistories.Any(history => history.UserId == currentUserId.Value && history.VideoId == video.VideoId));
                        if (promotedVideoCount > 0)
                            fallbackVideosQuery = fallbackVideosQuery.Where(video =>
                                !promotedVideosQuery!.Select(promoted => promoted.VideoId).Contains(video.VideoId));
                        if (recIds.Count > 0)
                            fallbackVideosQuery = fallbackVideosQuery.Where(video => !recIds.Contains(video.VideoId));

                        var fallbackRowsQuery = FeedRows(fallbackVideosQuery, isPromoted: false);
                        fallbackRowsQuery = fallbackRowsQuery
                            .OrderByDescending(row => row.Views ?? 0L).ThenByDescending(row => row.PublishedAt).ThenBy(row => row.VideoId);
                        var fallbackRows = await fallbackRowsQuery
                            .Skip(fallbackSkip).Take(pageSize - personalizedPageRows.Count).ToListAsync(ct);
                        personalizedPageRows.AddRange(fallbackRows);
                    }

                    if (personalizedPageRows.Count > 0)
                    {
                        var totalCount = await VideoAccessPolicy.Catalogue(db)
                            .Where(video => !db.ViewingHistories.Any(history => history.UserId == currentUserId.Value && history.VideoId == video.VideoId))
                            .CountAsync(ct);
                        return new(await ToFeedCardsAsync(personalizedPageRows), page, pageSize, totalCount);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error while processing personalized feed for user {UserId}. Falling back to popular public videos.", currentUserId.Value);
            }

            // If CF has no usable result (for example when Recommendation Service is down),
            // keep the fallback deterministic and useful by ordering the public catalogue by views.
        }

        var query = VideoAccessPolicy.Catalogue(db).Where(x => x.Status == "published" && x.ModerationStatus == "approved" && x.Visibility == "public");
        if (hideViewed)
            query = query.Where(x => !db.ViewingHistories.Any(history => history.UserId == currentUserId!.Value && history.VideoId == x.VideoId));
        if (categoryId.HasValue) query = query.Where(x => x.CategoryId == categoryId);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var normalizedTag = tag.Trim();
            query = query.Where(x => db.VideoTags.Any(videoTag => videoTag.VideoId == x.VideoId &&
                db.Tags.Any(tagRow => tagRow.TagId == videoTag.TagId && EF.Functions.ILike(tagRow.Name, normalizedTag))));
        }
        var filteredTotal = await query.CountAsync(ct);
        var hasColdStartFilter = categoryId.HasValue || !string.IsNullOrWhiteSpace(tag);
        var fallbackToDefault = hasColdStartFilter && filteredTotal == 0;
        var total = filteredTotal;
        if (fallbackToDefault)
        {
            // Guest cold-start falls back from Category/Tag to the public newest/popular catalogue.
            query = VideoAccessPolicy.Catalogue(db).Where(x => x.Status == "published" && x.ModerationStatus == "approved" && x.Visibility == "public");
            if (hideViewed)
                query = query.Where(x => !db.ViewingHistories.Any(history => history.UserId == currentUserId!.Value && history.VideoId == x.VideoId));
            total = await query.CountAsync(ct);
        }
        if (promotedVideoCount > 0)
            query = query.Where(video => !promotedVideosQuery!.Select(promoted => promoted.VideoId).Contains(video.VideoId));
        var rows = FeedRows(query, isPromoted: false);
        rows = (feed.ToLowerInvariant(), sort?.ToLowerInvariant()) switch
        {
            ("home", "random") => rows.OrderBy(_ => EF.Functions.Random()),
            ("home", _) => rows.OrderByDescending(x => x.Views ?? 0L).ThenByDescending(x => x.PublishedAt).ThenBy(x => x.VideoId),
            (_, "popular") => rows.OrderByDescending(x => x.Views ?? 0L).ThenByDescending(x => x.PublishedAt).ThenBy(x => x.VideoId),
            (_, "trending") => rows.OrderByDescending(x => x.Views ?? 0L).ThenByDescending(x => x.PublishedAt).ThenBy(x => x.VideoId),
            _ => rows.OrderByDescending(x => x.PublishedAt).ThenBy(x => x.VideoId)
        };
        if (promotedVideoCount > 0)
        {
            var pageOffset = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
            var promotionSkip = Math.Min(pageOffset, promotedVideoCount);
            var promotionTake = Math.Min(pageSize, promotedVideoCount - promotionSkip);
            var promotionRows = await FeedRows(promotedVideosQuery!, isPromoted: true)
                .OrderByDescending(row => row.PublishedAt).ThenBy(row => row.VideoId)
                .Skip(promotionSkip).Take(promotionTake).ToListAsync(ct);
            var organicOffset = Math.Max(0, pageOffset - promotedVideoCount);
            var organicRows = await rows.Skip(organicOffset).Take(pageSize - promotionRows.Count).ToListAsync(ct);
            return new(await ToFeedCardsAsync(promotionRows.Concat(organicRows)), page, pageSize, total);
        }

        var pageRows = await rows.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(await ToFeedCardsAsync(pageRows), page, pageSize, total);
    }

    public async Task<PageResult<VideoCardResponse>> GetSubscriptionsFeedAsync(Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        var (p, size) = Page(page, pageSize);
        var subscribedChannelIds = db.Subscriptions.AsNoTracking()
            .Where(s => s.UserId == userId && s.Status == "active")
            .Select(s => s.ChannelId)
            .Distinct();

        if (!await subscribedChannelIds.AnyAsync(ct))
        {
            return new([], p, size, 0);
        }

        var query = VideoAccessPolicy.Catalogue(db)
            .Where(x => subscribedChannelIds.Contains(x.ChannelId) && x.Status == "published" && x.ModerationStatus == "approved" && x.Visibility == "public");

        var total = await query.CountAsync(ct);
        var subscriptionViewCounts = db.ViewingHistories.AsNoTracking().GroupBy(history => history.VideoId)
            .Select(group => new { VideoId = group.Key, Count = group.LongCount() });
        var rows = from video in query
                   join channel in db.Channels.AsNoTracking() on video.ChannelId equals channel.ChannelId
                   join view in subscriptionViewCounts on video.VideoId equals view.VideoId into viewRows
                   from view in viewRows.DefaultIfEmpty()
                   orderby video.PublishedAt.HasValue descending, video.PublishedAt descending, video.VideoId descending
                   select new
                   {
                       video.VideoId,
                       video.ChannelId,
                       ChannelName = channel.Name,
                       ChannelHandle = channel.Handle,
                       video.Title,
                       video.ThumbnailUrl,
                       video.Duration,
                       video.Visibility,
                       video.PublishedAt,
                        Views = (long?)view.Count
                   };

        var pageRows = await rows.Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var cards = new List<VideoCardResponse>();
        foreach (var row in pageRows)
        {
            cards.Add(new(row.VideoId, row.ChannelId, row.ChannelName, row.ChannelHandle,
                row.Title, row.ThumbnailUrl == null ? null : await ReadUrlAsync(row.ThumbnailUrl, ct), row.Duration, row.Visibility, row.PublishedAt, row.Views ?? 0L));
        }
        return new(cards, p, size, total);
    }

    private IQueryable<Video> SearchCatalogue(SearchVideosQuery search)
    {
        var query = VideoAccessPolicy.Catalogue(db).Where(x => x.Status == "published" && x.ModerationStatus == "approved" && x.Visibility == "public");

        var term = search.Query?.Trim();
        var cleanTerm = term?.TrimStart('@');
        if (!string.IsNullOrWhiteSpace(term))
        {
            var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length <= 1)
            {
                query = query.Where(x =>
                    EF.Functions.ILike(EF.Functions.Unaccent(x.Title), EF.Functions.Unaccent($"%{term}%")) ||
                    (x.Description != null && EF.Functions.ILike(EF.Functions.Unaccent(x.Description), EF.Functions.Unaccent($"%{term}%"))) ||
                    db.Channels.Any(c => c.ChannelId == x.ChannelId && (
                        EF.Functions.ILike(EF.Functions.Unaccent(c.Name), EF.Functions.Unaccent($"%{term}%")) ||
                        EF.Functions.ILike(EF.Functions.Unaccent(c.Handle), EF.Functions.Unaccent($"%{term}%")) ||
                        (cleanTerm != null && EF.Functions.ILike(EF.Functions.Unaccent(c.Handle), EF.Functions.Unaccent($"%{cleanTerm}%"))))) ||
                    db.VideoTags.Any(vt => vt.VideoId == x.VideoId && db.Tags.Any(t => t.TagId == vt.TagId && EF.Functions.ILike(EF.Functions.Unaccent(t.Name), EF.Functions.Unaccent($"%{term}%"))))
                );
            }
            else
            {
                foreach (var token in tokens)
                {
                    var t = token.TrimStart('@');
                    query = query.Where(x =>
                        EF.Functions.ILike(EF.Functions.Unaccent(x.Title), EF.Functions.Unaccent($"%{token}%")) ||
                        (x.Description != null && EF.Functions.ILike(EF.Functions.Unaccent(x.Description), EF.Functions.Unaccent($"%{token}%"))) ||
                        db.Channels.Any(c => c.ChannelId == x.ChannelId && (
                            EF.Functions.ILike(EF.Functions.Unaccent(c.Name), EF.Functions.Unaccent($"%{token}%")) ||
                            EF.Functions.ILike(EF.Functions.Unaccent(c.Handle), EF.Functions.Unaccent($"%{token}%")) ||
                            EF.Functions.ILike(EF.Functions.Unaccent(c.Handle), EF.Functions.Unaccent($"%{t}%")))) ||
                        db.VideoTags.Any(vt => vt.VideoId == x.VideoId && db.Tags.Any(tTag => tTag.TagId == vt.TagId && EF.Functions.ILike(EF.Functions.Unaccent(tTag.Name), EF.Functions.Unaccent($"%{token}%"))))
                    );
                }
            }
        }

        if (search.CategoryId.HasValue)
        {
            query = query.Where(x => x.CategoryId == search.CategoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search.Tag))
        {
            var normalizedTag = search.Tag.Trim().TrimStart('#');
            query = query.Where(x => db.VideoTags.Any(vt => vt.VideoId == x.VideoId &&
                db.Tags.Any(t => t.TagId == vt.TagId && EF.Functions.ILike(t.Name, normalizedTag))));
        }

        if (search.ChannelId.HasValue)
        {
            query = query.Where(x => x.ChannelId == search.ChannelId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search.DateRange))
        {
            var now = Now;
            var todayUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var weekAgo = now.AddDays(-7);
            var monthAgo = now.AddDays(-30);
            var yearAgo = now.AddDays(-365);

            query = search.DateRange.Trim().ToLowerInvariant() switch
            {
                "today" or "day" => query.Where(x => x.PublishedAt.HasValue && x.PublishedAt.Value >= todayUtc),
                "this_week" or "week" => query.Where(x => x.PublishedAt.HasValue && x.PublishedAt.Value >= weekAgo),
                "this_month" or "month" => query.Where(x => x.PublishedAt.HasValue && x.PublishedAt.Value >= monthAgo),
                "this_year" or "year" => query.Where(x => x.PublishedAt.HasValue && x.PublishedAt.Value >= yearAgo),
                _ => query
            };
        }

        if (!string.IsNullOrWhiteSpace(search.DurationRange))
        {
            query = search.DurationRange.Trim().ToLowerInvariant() switch
            {
                "short" => query.Where(x => x.Duration < 240),
                "medium" => query.Where(x => x.Duration >= 240 && x.Duration <= 1200),
                "long" => query.Where(x => x.Duration > 1200),
                _ => query
            };
        }

        return query;
    }

    public async Task<IReadOnlyList<SearchChannelResponse>> GetSearchChannelsAsync(SearchVideosQuery search, CancellationToken ct = default)
    {
        // Include matching channels from every page; selecting one must not erase the other options.
        var counts = SearchCatalogue(search with { ChannelId = null })
            .GroupBy(video => video.ChannelId)
            .Select(group => new { ChannelId = group.Key, VideoCount = group.Count() });
        return await (from count in counts
                      join channel in db.Channels.AsNoTracking() on count.ChannelId equals channel.ChannelId
                      orderby channel.Name, channel.Handle
                      select new SearchChannelResponse(channel.ChannelId, channel.Name, channel.Handle, count.VideoCount)).ToListAsync(ct);
    }

    public async Task<PageResult<ChannelSearchResultResponse>> SearchChannelsAsync(
        string? query, int page, int pageSize, CancellationToken ct = default)
    {
        (page, pageSize) = Page(page, pageSize);
        var term = (query ?? "").Trim().TrimStart('@');
        var channels = db.Channels.AsNoTracking().Where(channel => channel.Status == "active");
        // Search channel identity directly, including channels with no published videos.
        foreach (var token in term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var escaped = token.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = $"%{escaped}%";
            channels = channels.Where(channel => EF.Functions.ILike(channel.Name, pattern, "\\")
                || EF.Functions.ILike(channel.Handle, pattern, "\\"));
        }
        var total = await channels.CountAsync(ct);
        var normalizedTerm = term.ToLowerInvariant();
        var publicVideos = VideoAccessPolicy.Catalogue(db);
        var rows = await channels.Select(channel => new
            {
                channel.ChannelId, channel.Name, channel.Handle, channel.Description, channel.AvatarUrl,
                ExactMatch = normalizedTerm != "" && (channel.Name.ToLower() == normalizedTerm || channel.Handle.ToLower() == normalizedTerm),
                PrefixMatch = normalizedTerm != "" && (channel.Name.ToLower().StartsWith(normalizedTerm) || channel.Handle.ToLower().StartsWith(normalizedTerm)),
                SubscriberCount = db.Subscriptions.LongCount(subscription => subscription.ChannelId == channel.ChannelId && subscription.Status == "active"),
                VideoCount = publicVideos.Count(video => video.ChannelId == channel.ChannelId)
            })
            .OrderByDescending(channel => channel.ExactMatch)
            .ThenByDescending(channel => channel.PrefixMatch)
            .ThenByDescending(channel => channel.SubscriberCount)
            .ThenBy(channel => channel.Name).ThenBy(channel => channel.ChannelId)
            .Skip((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue)).Take(pageSize)
            .ToListAsync(ct);
        var items = new List<ChannelSearchResultResponse>(rows.Count);
        foreach (var channel in rows)
            items.Add(new(channel.ChannelId, channel.Name, channel.Handle, channel.Description,
                channel.AvatarUrl == null ? null : await ReadUrlAsync(channel.AvatarUrl, ct),
                channel.SubscriberCount, channel.VideoCount));
        return new(items, page, pageSize, total);
    }

    public async Task<PageResult<VideoCardResponse>> SearchVideosAsync(SearchVideosQuery search, CancellationToken ct = default)
    {
        var (page, pageSize) = Page(search.Page, search.PageSize);
        var query = SearchCatalogue(search);
        var term = search.Query?.Trim();
        var cleanTerm = term?.TrimStart('@');

        var total = await query.CountAsync(ct);

        var viewCounts = db.ViewingHistories.AsNoTracking().GroupBy(x => x.VideoId)
            .Select(group => new { VideoId = group.Key, Count = group.LongCount() });
        var likeCounts = db.VideoReactions.AsNoTracking().Where(x => x.Type == "like").GroupBy(x => x.VideoId)
            .Select(group => new { VideoId = group.Key, Count = group.LongCount() });
        var commentCounts = db.Comments.AsNoTracking().Where(x => x.Status == "visible").GroupBy(x => x.VideoId)
            .Select(group => new { VideoId = group.Key, Count = group.LongCount() });
        var rows = from video in query
                   join channel in db.Channels.AsNoTracking() on video.ChannelId equals channel.ChannelId
                   join view in viewCounts on video.VideoId equals view.VideoId into viewRows
                   from view in viewRows.DefaultIfEmpty()
                   join like in likeCounts on video.VideoId equals like.VideoId into likeRows
                   from like in likeRows.DefaultIfEmpty()
                   join comment in commentCounts on video.VideoId equals comment.VideoId into commentRows
                   from comment in commentRows.DefaultIfEmpty()
                   select new
                   {
                       video.VideoId,
                       video.ChannelId,
                       ChannelName = channel.Name,
                       ChannelHandle = channel.Handle,
                       video.Title,
                       video.ThumbnailUrl,
                       video.Duration,
                       video.Visibility,
                       video.PublishedAt,
                       Views = (long?)view.Count,
                       Likes = (long?)like.Count,
                       Comments = (long?)comment.Count
                   };

        var sort = search.Sort?.Trim().ToLowerInvariant() ?? "relevance";
        rows = sort switch
        {
            "newest" => rows.OrderByDescending(x => x.PublishedAt).ThenBy(x => x.VideoId),
            "oldest" => rows.OrderBy(x => x.PublishedAt).ThenBy(x => x.VideoId),
            "views" or "popular" => rows.OrderByDescending(x => x.Views ?? 0L).ThenByDescending(x => x.PublishedAt).ThenBy(x => x.VideoId),
            "engagement" => rows.OrderByDescending(x => (x.Likes ?? 0L) + (x.Comments ?? 0L)).ThenByDescending(x => x.Views ?? 0L),
            "random" => rows.OrderBy(_ => EF.Functions.Random()),
            _ when !string.IsNullOrWhiteSpace(term) =>
                rows.OrderByDescending(x => EF.Functions.ILike(EF.Functions.Unaccent(x.Title), EF.Functions.Unaccent($"{term}%")))
                    .ThenByDescending(x => EF.Functions.ILike(EF.Functions.Unaccent(x.Title), EF.Functions.Unaccent($"%{term}%")))
                    .ThenByDescending(x => EF.Functions.ILike(EF.Functions.Unaccent(x.ChannelName), EF.Functions.Unaccent($"%{term}%")) || (cleanTerm != null && EF.Functions.ILike(EF.Functions.Unaccent(x.ChannelHandle), EF.Functions.Unaccent($"%{cleanTerm}%"))))
                    .ThenByDescending(x => x.Views ?? 0L)
                    .ThenByDescending(x => x.PublishedAt),
            _ => rows.OrderByDescending(x => x.Views ?? 0L).ThenByDescending(x => x.PublishedAt)
        };

        var pageRows = await rows.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var cards = new List<VideoCardResponse>();
        foreach (var row in pageRows)
        {
            cards.Add(new(
                row.VideoId,
                row.ChannelId,
                row.ChannelName,
                row.ChannelHandle,
                row.Title,
                row.ThumbnailUrl == null ? null : await ReadUrlAsync(row.ThumbnailUrl, ct),
                row.Duration,
                row.Visibility,
                row.PublishedAt,
                row.Views ?? 0L));
        }

        return new(cards, page, pageSize, total);
    }

    public async Task<PageResult<WatchHistoryResponse>> GetWatchHistoryAsync(Guid userId, int page, int pageSize,
        DateTimeOffset? before = null, CancellationToken ct = default, string? q = null, string sort = "newest", string? dateRange = null)
    {
        (page, pageSize) = Page(page, pageSize);
        var windowEnd = before.HasValue && before.Value < Now ? before.Value : Now;
        if (sort is not ("newest" or "oldest") || dateRange is not (null or "all" or "today" or "week" or "month") || q?.Length > 200)
            throw new ContentException(400, "INVALID_HISTORY_FILTER", "Bộ lọc lịch sử không hợp lệ.");
        DateTimeOffset? windowStart = dateRange switch
        {
            "all" => null,
            "today" => new DateTimeOffset(windowEnd.UtcDateTime.Date, TimeSpan.Zero),
            "week" => windowEnd.AddDays(-7),
            _ => windowEnd.AddDays(-30)
        };
        // Keep only the newest row for each video. A correlated NOT EXISTS is
        // translated reliably by PostgreSQL, unlike joining a GroupBy/First
        // projection to the video query.
        var latest = db.ViewingHistories.AsNoTracking()
            .Where(history => history.UserId == userId)
            .Where(history => !db.ViewingHistories.Any(other =>
                other.UserId == userId &&
                other.VideoId == history.VideoId &&
                  (other.ViewedAt > history.ViewedAt ||
                  (other.ViewedAt == history.ViewedAt && other.ViewingHistoryId > history.ViewingHistoryId))));
        latest = latest.Where(history => history.ViewedAt < windowEnd);
        if (windowStart.HasValue) latest = latest.Where(history => history.ViewedAt >= windowStart.Value);
        var query = from history in latest
                    join video in LibraryVisibleVideos(userId) on history.VideoId equals video.VideoId
                    join channel in db.Channels.AsNoTracking() on video.ChannelId equals channel.ChannelId
                    select new { video.VideoId, video.ChannelId, ChannelName = channel.Name, ChannelHandle = channel.Handle,
                        video.Title, video.ThumbnailUrl, video.Duration, history.WatchDuration, history.Progress, history.ViewedAt };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(row => row.Title.ToLower().Contains(term) || row.ChannelName.ToLower().Contains(term) || row.ChannelHandle.ToLower().Contains(term));
        }
        var ordered = sort == "oldest"
            ? query.OrderBy(row => row.ViewedAt).ThenBy(row => row.VideoId)
            : query.OrderByDescending(row => row.ViewedAt).ThenBy(row => row.VideoId);
        var total = await query.CountAsync(ct);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = new List<WatchHistoryResponse>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(new(row.VideoId, row.ChannelId, row.ChannelName, row.ChannelHandle, row.Title,
                row.ThumbnailUrl == null ? null : await ReadUrlAsync(row.ThumbnailUrl, ct), row.Duration,
                row.WatchDuration, row.Progress, row.ViewedAt));
        }

        return new(items, page, pageSize, total);
    }

    public async Task ClearWatchHistoryAsync(Guid userId, CancellationToken ct = default)
    {
        var items = await db.ViewingHistories.Where(x => x.UserId == userId).ToListAsync(ct);
        if (items.Count > 0)
        {
            db.ViewingHistories.RemoveRange(items);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteWatchHistoryItemAsync(Guid userId, Guid videoId, CancellationToken ct = default)
    {
        var items = await db.ViewingHistories.Where(x => x.UserId == userId && x.VideoId == videoId).ToListAsync(ct);
        if (items.Count > 0)
        {
            db.ViewingHistories.RemoveRange(items);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<PageResult<LibraryVideoResponse>> GetLikedVideosAsync(Guid userId, int? rating, int page, int pageSize, CancellationToken ct = default)
    {
        if (rating is < 1 or > 5)
            throw Error(400, "INVALID_RATING_FILTER", "Bộ lọc đánh giá phải từ 1 đến 5 sao.");

        (page, pageSize) = Page(page, pageSize);
        var query = from reaction in db.VideoReactions.AsNoTracking()
                    where reaction.UserId == userId && reaction.Type == "like"
                    join video in LibraryVisibleVideos(userId) on reaction.VideoId equals video.VideoId
                    select new { Video = video, reaction.UpdatedAt };
        if (rating.HasValue)
        {
            var selectedRating = rating.Value;
            query = query.Where(row => db.VideoRatings.Any(videoRating =>
                videoRating.UserId == userId && videoRating.VideoId == row.Video.VideoId && videoRating.Score == selectedRating));
        }
        query = query.OrderByDescending(row => row.UpdatedAt);

        var total = await query.CountAsync(ct);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var details = await ToResponsesAsync(rows.Select(row => row.Video).ToList(), userId, ct);
        var detailById = details.ToDictionary(detail => detail.VideoId);
        var items = new List<LibraryVideoResponse>(rows.Count);
        foreach (var row in rows)
        {
            var detail = detailById[row.Video.VideoId];
            var state = detail.ViewerState;
            items.Add(ToLibraryResponse(detail, state?.ResumeAtSeconds ?? 0, state?.Progress ?? 0, row.UpdatedAt));
        }

        return new(items, page, pageSize, total);
    }

    public async Task<VideoResponse> GetVideoAsync(Guid videoId, Guid? viewerId, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct);
        await EnsureCanViewAsync(video, viewerId, ct);
        return await ToResponseAsync(video, viewerId, ct);
    }

    public async Task<PlaybackResponse> GetPlaybackAsync(Guid videoId, Guid? viewerId, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct);
        await EnsureCanViewAsync(video, viewerId, ct);
        var maxHeight = viewerId.HasValue ? await MaxDownloadHeightAsync(viewerId.Value, ct) : 720;
        var renditionRows = await db.VideoRenditions.AsNoTracking().Where(x => x.VideoId == videoId && x.Status == "ready" && x.Height <= maxHeight).OrderBy(x => x.Height).ToListAsync(ct);
        var renditions = new List<RenditionResponse>();
        foreach (var row in renditionRows) renditions.Add(new(row.QualityLabel, row.Width, row.Height, row.FileSize, await ReadUrlAsync(row.FileUrl, ct)));
        var history = viewerId.HasValue ? await db.ViewingHistories.AsNoTracking().Where(x => x.UserId == viewerId && x.VideoId == videoId)
            .OrderByDescending(x => x.ViewedAt).FirstOrDefaultAsync(ct) : null;
        return new(video.VideoId, video.Title, video.Visibility, video.Duration, renditions, history?.WatchDuration ?? 0, history?.Progress ?? 0);
    }

    public async Task<VideoResponse> CreateVideoAsync(Guid actorId, CreateVideoCommand command, CancellationToken ct = default)
    {
        var channel = await RequireChannelPermissionAsync(command.ChannelId, actorId, ChannelPermissions.VideoUpload, ct);
        var idempotencyKey = command.IdempotencyKey?.Trim();
        if (idempotencyKey is { Length: > 128 }) throw Error(400, "INVALID_IDEMPOTENCY_KEY", "Idempotency-Key không được vượt quá 128 ký tự.");
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var previous = await db.Videos.AsNoTracking().SingleOrDefaultAsync(x => x.ChannelId == command.ChannelId && x.IdempotencyKey == idempotencyKey, ct);
            if (previous != null) return await ToResponseAsync(previous, actorId, ct);
        }
        if (string.IsNullOrWhiteSpace(command.Title) || command.Title.Trim().Length > 100)
            throw Error(400, "INVALID_TITLE", "Tiêu đề cần từ 1 đến 100 ký tự.");
        if (command.Duration <= 0) throw Error(400, "INVALID_DURATION", "Thời lượng video phải lớn hơn 0.");
        VideoRules.ValidateVisibility(command.Visibility);
        EnsureVisibilityAvailable(command.Visibility);
        if (command.CategoryId.HasValue && !await db.Categories.AnyAsync(x => x.CategoryId == command.CategoryId && x.Status == "active", ct))
            throw Error(400, "INVALID_CATEGORY", "Danh mục không tồn tại hoặc đã ngừng hoạt động.");

        await PreflightAsync(actorId, new(command.ChannelId, command.FileSize, command.Duration, command.ContentType, command.SourceQuality), ct);

        var quota = await db.ChannelQuotas.SingleOrDefaultAsync(x => x.ChannelId == channel.ChannelId, ct);
        // Upload limits belong to the channel owner’s subscription. A Manager/Editor
        // may upload on behalf of the channel, but must not downgrade or bypass the
        // owner’s quota simply because the actor has a different plan.
        if (quota != null)
        {
            if (!ChannelQuotaRules.CanReserve(quota.StorageUsed, command.FileSize, quota.StorageLimit))
                throw Error(409, "CHANNEL_QUOTA_EXCEEDED", "Kênh không còn đủ dung lượng lưu trữ.");
        }

        IReadOnlyList<VideoChapter> chapters;
        try { chapters = VideoRules.ValidateChapters(command.Chapters.Select(x => new VideoChapter(x.StartSeconds, x.Title)), command.Duration); }
        catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
        IReadOnlyList<VideoCardRequest> videoCards = [];
        var tags = NormalizeTags(command.Tags);
        var videoId = Guid.NewGuid();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "hutube-video-processing", $"video-{videoId:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var sourceExtension = Path.GetExtension(command.FileName);
        if (string.IsNullOrWhiteSpace(sourceExtension)) sourceExtension = ".mp4";
        var sourceFile = Path.Combine(temporaryDirectory, "source" + sourceExtension);
        ProbedVideo? probe = null;
        string? videoUrl = null;
        string? thumbnailUrl = null;
        try
        {
            await using (var local = File.Create(sourceFile)) await command.Content.CopyToAsync(local, ct);
            probe = await transcoder.ProbeAsync(sourceFile, ct);
            var actualSize = new FileInfo(sourceFile).Length;
            var detectedQualityHeight = new[] { 360, 480, 720, 1080, 1440, 2160 }.FirstOrDefault(height => height >= probe.Height);
            if (detectedQualityHeight == 0) throw Error(400, "INVALID_SOURCE_QUALITY", "Video vượt chất lượng hỗ trợ.");
            var requestedQualityHeight = VideoRules.QualityHeight(command.SourceQuality);
            if (requestedQualityHeight == 0) throw Error(400, "INVALID_SOURCE_QUALITY", "Chất lượng video không hợp lệ.");
            var sourceQualityHeight = Math.Min(detectedQualityHeight, requestedQualityHeight);
            var sourceQuality = sourceQualityHeight + "p";
            await PreflightAsync(actorId, new(command.ChannelId, actualSize, probe.Duration, command.ContentType, sourceQuality), ct);
            if (probe.Height > sourceQualityHeight)
            {
                var reducedSource = await transcoder.CreateRenditionAsync(
                    sourceFile, detectedQualityHeight + "p", sourceQuality, temporaryDirectory, ct);
                if (reducedSource is null || !File.Exists(reducedSource.FilePath) || new FileInfo(reducedSource.FilePath).Length <= 0)
                    throw Error(503, "UPLOAD_QUALITY_REDUCTION_UNAVAILABLE", "Không thể giảm chất lượng video theo giới hạn gói.");
                sourceFile = reducedSource.FilePath;
                probe = await transcoder.ProbeAsync(sourceFile, ct);
                if (probe.Height > sourceQualityHeight)
                    throw Error(503, "UPLOAD_QUALITY_REDUCTION_FAILED", "Video sau khi giảm chất lượng vẫn vượt giới hạn gói.");
                actualSize = new FileInfo(sourceFile).Length;
                command = command with
                {
                    FileName = $"{Path.GetFileNameWithoutExtension(command.FileName)}.mp4",
                    ContentType = "video/mp4"
                };
            }
            command = command with { Duration = probe.Duration, FileSize = actualSize, SourceQuality = sourceQuality };
            try { chapters = VideoRules.ValidateChapters(command.Chapters.Select(x => new VideoChapter(x.StartSeconds, x.Title)), command.Duration); }
            catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
            videoCards = await ValidateVideoCardsAsync(channel.ChannelId, command.VideoCards, command.Duration, ct);
            if (command.ThumbnailContent != null && command.ThumbnailFileName != null && command.ThumbnailContentType != null)
                thumbnailUrl = await storage.SaveFileAsync("video-thumbnails", command.ThumbnailFileName, command.ThumbnailContent, command.ThumbnailContentType, ct);
            else
            {
                string? generatedThumbnailPath = null;
                try
                {
                    generatedThumbnailPath = await transcoder.CreateThumbnailAsync(sourceFile, temporaryDirectory, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A thumbnail is optional; a missing FFmpeg binary or an
                    // unsupported codec must not discard an otherwise valid upload.
                    logger.LogWarning(ex, "Không thể tự tạo thumbnail cho video {FileName}; tiếp tục upload không có thumbnail.", command.FileName);
                }
                if (!string.IsNullOrWhiteSpace(generatedThumbnailPath))
                {
                    await using var generatedThumbnail = File.OpenRead(generatedThumbnailPath);
                    thumbnailUrl = await storage.SaveFileAsync(
                        "video-thumbnails", $"{Guid.NewGuid():N}.jpg", generatedThumbnail, "image/jpeg", ct);
                }
            }
            await using (var local = File.OpenRead(sourceFile))
                videoUrl = await storage.SaveVideoAsync("videos", command.FileName, local, command.ContentType, ct);
        }
        catch (Exception ex)
        {
            if (videoUrl != null) await storage.DeleteFileAsync(videoUrl, CancellationToken.None);
            if (thumbnailUrl != null) await storage.DeleteFileAsync(thumbnailUrl, CancellationToken.None);
            try { Directory.Delete(temporaryDirectory, true); }
            catch (IOException cleanupEx) { logger.LogWarning(cleanupEx, "Không thể xóa thư mục xử lý video {Directory}", temporaryDirectory); }
            if (ex is not OperationCanceledException)
                await RecordUploadFailureAsync(actorId, videoId, command.Title, ex);
            throw;
        }

        var now = Now;
        var publicUploadRequiresModeration = features.ModerationEnabled
            && command.Visibility.Equals("public", StringComparison.OrdinalIgnoreCase);
        var video = new Video { VideoId = videoId, ChannelId = command.ChannelId, UploadedByUserId = actorId, CategoryId = command.CategoryId, Title = command.Title.Trim(), Description = Clean(command.Description),
            VideoUrl = videoUrl!, ThumbnailUrl = thumbnailUrl, Duration = command.Duration, FileSize = command.FileSize, Visibility = command.Visibility.ToLowerInvariant(),
            Status = "processing", LanguageCode = Clean(command.LanguageCode), AgeRestricted = command.AgeRestricted, AllowComments = command.AllowComments,
            ModerationStatus = publicUploadRequiresModeration ? "pending" : "not_submitted",
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
            Metadata = PersistenceJson.Serialize(new { chapters, videoCards }), CreatedAt = now, UpdatedAt = now };
        var generated = new List<(TranscodedVideo Rendition, string StoredPath)>();
        Exception? processingError = null;
        var deferredProcessingQueued = false;
        if (command.GenerateLowerRenditions && !command.DeferLowerRenditions)
        {
            try
            {
                foreach (var rendition in await transcoder.CreateLowerRenditionsAsync(sourceFile, command.SourceQuality, temporaryDirectory, ct))
                {
                    await using var renditionStream = File.OpenRead(rendition.FilePath);
                    var storedPath = await storage.SaveVideoAsync($"video-renditions/{video.VideoId:N}", $"{rendition.Quality}.mp4", renditionStream, "video/mp4", ct);
                    generated.Add((rendition, storedPath));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                processingError = ex;
                logger.LogError(ex, "Không thể tạo đủ rendition cho video {VideoId}", video.VideoId);
            }
        }

        if (processingError != null) video.Status = "failed";
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // Serialize membership transitions and all uploads charged to the same
            // owner, then re-resolve the owner after waiting for the membership lock.
            await LockStorageAsync($"plan-membership:{channel.OwnerUserId}", ct);
            var billingOwnerUserId = await GetStorageBillingOwnerAsync(channel.OwnerUserId, ct);
            await LockStorageAsync($"plan-storage:{billingOwnerUserId}", ct);
            if (quota != null)
            {
                await LockStorageAsync($"quota:{channel.ChannelId}", ct);
                await db.Entry(quota).ReloadAsync(ct);
                if (!ChannelQuotaRules.CanReserve(quota.StorageUsed, command.FileSize, quota.StorageLimit))
                    throw Error(409, "CHANNEL_QUOTA_EXCEEDED", "Kênh không còn đủ dung lượng lưu trữ.");
                quota.StorageUsed += command.FileSize;
                quota.UpdatedAt = now;
            }
            if (features.PlanEnforcementEnabled)
            {
                var groupPlan = await GetEffectivePlanForUserAsync(billingOwnerUserId, ct);
                var groupStorageLimit = groupPlan?.StorageLimit ?? ChannelQuotaRules.DefaultStorageLimit;
                var groupStorageUsed = await GetBilledStorageAsync(billingOwnerUserId, ct);
                if (!ChannelQuotaRules.CanReserve(groupStorageUsed, command.FileSize, groupStorageLimit))
                    throw Error(409, "SHARED_GROUP_QUOTA_EXCEEDED", "Nhóm đã hết dung lượng gói chung. Chủ gói cần tăng dung lượng hoặc xóa video.");
            }
            video.StorageBillingOwnerUserId = billingOwnerUserId;
            db.Videos.Add(video);
            await db.SaveChangesAsync(ct);

            if (publicUploadRequiresModeration)
                db.ModerationCases.Add(CreateUploadModerationCase(video, now));

            db.VideoRenditions.Add(new VideoRendition { VideoId = video.VideoId, QualityLabel = command.SourceQuality.ToLowerInvariant(), Width = probe!.Width,
                Height = probe.Height, FileUrl = videoUrl!, FileSize = command.FileSize, Codec = "source", Status = "ready", CreatedAt = now, UpdatedAt = now });
            if (command.DeferLowerRenditions)
            {
                foreach (var quality in VideoRules.LowerQualities(command.SourceQuality))
                {
                    var height = VideoRules.QualityHeight(quality);
                    db.VideoRenditions.Add(new VideoRendition { VideoId = video.VideoId, QualityLabel = quality, Width = height * 16 / 9,
                        Height = height, FileUrl = videoUrl!, FileSize = command.FileSize, Codec = "h264/aac", Status = "processing", CreatedAt = now, UpdatedAt = now });
                }
            }
            else
            {
                foreach (var item in generated)
                    db.VideoRenditions.Add(new VideoRendition { VideoId = video.VideoId, QualityLabel = item.Rendition.Quality, Width = item.Rendition.Width,
                        Height = item.Rendition.Height, BitrateKbps = item.Rendition.BitrateKbps, FileUrl = item.StoredPath, FileSize = item.Rendition.FileSize,
                        Codec = "h264/aac", Status = "ready", CreatedAt = now, UpdatedAt = now });
                if (processingError != null)
                {
                    foreach (var quality in VideoRules.LowerQualities(command.SourceQuality).Except(generated.Select(x => x.Rendition.Quality)))
                    {
                        var height = VideoRules.QualityHeight(quality);
                        db.VideoRenditions.Add(new VideoRendition { VideoId = video.VideoId, QualityLabel = quality, Width = height * 16 / 9,
                            Height = height, FileUrl = videoUrl!, FileSize = command.FileSize, Codec = "h264/aac", Status = "failed", CreatedAt = now, UpdatedAt = now });
                    }
                    db.AuditLogs.Add(new AuditLog
                    {
                        ActorUserId = actorId,
                        Action = "video.rendition.processing.failed",
                        ResourceType = "video",
                        ResourceId = video.VideoId,
                        Reason = processingError.GetType().Name,
                        CreatedAt = now
                    });
                }
            }
            await ReplaceTagsAsync(video.VideoId, tags, ct); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            if (command.DeferLowerRenditions)
            {
                renditionQueue.Enqueue(new DeferredVideoRenditionJob(
                    video.VideoId, sourceFile, command.SourceQuality, temporaryDirectory));
                deferredProcessingQueued = true;
            }
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await storage.DeleteFileAsync(videoUrl!, CancellationToken.None);
            foreach (var item in generated) await storage.DeleteFileAsync(item.StoredPath, CancellationToken.None);
            if (thumbnailUrl != null) await storage.DeleteFileAsync(thumbnailUrl, CancellationToken.None);
            throw;
        }
        finally
        {
            if (!deferredProcessingQueued)
            {
                try { Directory.Delete(temporaryDirectory, true); }
                catch (IOException ex) { logger.LogWarning(ex, "Không thể xóa thư mục xử lý video {Directory}", temporaryDirectory); }
            }
        }
        return await ToResponseAsync(video, actorId, ct);
    }

    public async Task<UploadPreflightResponse> PreflightAsync(Guid actorId, UploadPreflightRequest request, CancellationToken ct = default)
    {
        var channel = await RequireChannelPermissionAsync(request.ChannelId, actorId, ChannelPermissions.VideoUpload, ct);
        var strikePolicy = await strikePolicySettings.GetAsync(ct);
        var activeStrikes = await db.ChannelStrikes.AsNoTracking()
            .Where(s => s.ChannelId == channel.ChannelId && s.Status == "active" && s.ExpiresAt > Now)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.CreatedAt, s.StrikeNumber, s.UploadRestrictedUntil, s.Reason })
            .ToListAsync(ct);
        var activeRestrictions = activeStrikes.Select(s => new
            {
                Until = s.UploadRestrictedUntil ?? s.CreatedAt.AddDays(StrikePolicySettingsService.RestrictionDaysFor(s.StrikeNumber, strikePolicy)),
                s.Reason
            })
            .Where(x => x.Until > Now).ToList();
        var restrictionEnd = activeRestrictions.Select(x => x.Until).DefaultIfEmpty().Max();
        if (restrictionEnd > Now)
        {
            var reason = activeRestrictions.OrderByDescending(x => x.Until).FirstOrDefault()?.Reason;
            var details = string.IsNullOrWhiteSpace(reason) ? "vi phạm chính sách" : $"lý do: {reason}";
            throw Error(403, "UPLOAD_RESTRICTED", $"Kênh đang bị tạm ngưng quyền đăng tải video đến {restrictionEnd:dd/MM/yyyy HH:mm}; {details}.");
        }

        var maxUpload = long.MaxValue;
        var maxDuration = int.MaxValue;
        var maxQuality = "2160p";
        Plan? plan = null;
        if (features.PlanEnforcementEnabled)
        {
            plan = await GetEffectivePlanForUserAsync(channel.OwnerUserId, ct);
            maxUpload = plan?.MaxUploadSize ?? 2L * 1024 * 1024 * 1024;
            maxDuration = plan?.MaxVideoDuration ?? 12 * 60 * 60;
            maxQuality = plan?.MaxVideoQuality ?? "720p";
        }
        try { VideoRules.ValidateUpload(request.ContentType, request.FileSize, maxUpload); }
        catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
        if (request.Duration <= 0 || request.Duration > maxDuration) throw Error(400, "VIDEO_TOO_LONG", "Video vượt quá thời lượng gói cho phép.");
        var sourceHeight = VideoRules.QualityHeight(request.SourceQuality);
        if (sourceHeight == 0) throw Error(400, "INVALID_VIDEO_QUALITY", "Chất lượng video không hợp lệ.");
        if (sourceHeight > VideoRules.QualityHeight(maxQuality))
        {
            if (!request.AllowQualityDowngrade)
                throw Error(403, "UPLOAD_QUALITY_DENIED", "Chất lượng video vượt quá giới hạn gói.");
            var downgradeQuota = await db.ChannelQuotas.AsNoTracking().SingleOrDefaultAsync(x => x.ChannelId == request.ChannelId, ct);
            var downgradeLimit = downgradeQuota?.StorageLimit ?? (features.PlanEnforcementEnabled
                ? plan?.StorageLimit ?? ChannelQuotaRules.DefaultStorageLimit
                : maxUpload);
            var downgradeUsed = downgradeQuota?.StorageUsed ?? 0;
            return new(false, maxUpload, maxDuration, maxQuality, downgradeLimit, downgradeUsed,
                ChannelQuotaRules.Remaining(downgradeLimit, downgradeUsed));
        }
        var quota = await db.ChannelQuotas.AsNoTracking().SingleOrDefaultAsync(x => x.ChannelId == request.ChannelId, ct);
        var limit = quota?.StorageLimit ?? (features.PlanEnforcementEnabled
            ? plan?.StorageLimit ?? ChannelQuotaRules.DefaultStorageLimit
            : maxUpload);
        var used = quota?.StorageUsed ?? 0;
        if (!ChannelQuotaRules.CanReserve(used, request.FileSize, limit)) throw Error(409, "CHANNEL_QUOTA_EXCEEDED", "Kênh không còn đủ dung lượng lưu trữ.");
        if (features.PlanEnforcementEnabled)
        {
            var billingOwnerUserId = await GetStorageBillingOwnerAsync(channel.OwnerUserId, ct);
            var groupPlan = billingOwnerUserId == channel.OwnerUserId ? plan : await GetEffectivePlanForUserAsync(billingOwnerUserId, ct);
            var groupStorageLimit = groupPlan?.StorageLimit ?? ChannelQuotaRules.DefaultStorageLimit;
            var groupStorageUsed = await GetBilledStorageAsync(billingOwnerUserId, ct);
            if (!ChannelQuotaRules.CanReserve(groupStorageUsed, request.FileSize, groupStorageLimit))
                throw Error(409, "SHARED_GROUP_QUOTA_EXCEEDED", "Nhóm đã hết dung lượng gói chung. Chủ gói cần tăng dung lượng hoặc xóa video.");
        }
        return new(true, maxUpload, maxDuration, maxQuality, limit, used, ChannelQuotaRules.Remaining(limit, used));
    }

    public async Task<PageResult<VideoResponse>> GetManagedVideosAsync(Guid actorId, Guid channelId, string? status, string? visibility, string? search, Guid? categoryId, string? tag, int page, int pageSize, CancellationToken ct = default)
    {
        await RequireChannelPermissionAsync(channelId, actorId, ChannelPermissions.VideoView, ct);
        (page, pageSize) = Page(page, pageSize);
        var query = db.Videos.AsNoTracking().Where(x => x.ChannelId == channelId && x.Status != "deleted");
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(visibility)) query = query.Where(x => x.Visibility == visibility);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); query = query.Where(x => EF.Functions.ILike(x.Title, $"%{term}%")); }
        if (categoryId.HasValue) query = query.Where(x => x.CategoryId == categoryId);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var normalizedTag = tag.Trim().TrimStart('#');
            query = query.Where(x => db.VideoTags.Any(vt => vt.VideoId == x.VideoId && db.Tags.Any(t => t.TagId == vt.TagId && EF.Functions.ILike(t.Name, normalizedTag))));
        }
        var total = await query.CountAsync(ct);
        var videos = await query.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = await ToResponsesAsync(videos, actorId, ct);
        return new(items, page, pageSize, total);
    }

    public async Task<VideoResponse> UpdateVideoAsync(Guid actorId, Guid videoId, UpdateVideoRequest request, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct);
        await RequireChannelPermissionAsync(video.ChannelId, actorId, ChannelPermissions.VideoEdit, ct);
        if (request.Title != null)
        {
            var title = request.Title.Trim();
            if (title.Length is < 1 or > 100) throw Error(400, "INVALID_TITLE", "Tiêu đề cần từ 1 đến 100 ký tự.");
            video.Title = title;
        }
        if (request.Description != null) video.Description = Clean(request.Description);
        if (request.Visibility != null)
        {
            var oldVisibility = video.Visibility;
            var newVisibility = request.Visibility.ToLowerInvariant();
            if (video.ModerationHidden && newVisibility != "private")
                throw Error(403, "MODERATION_RESTRICTION", "Video đang bị hạn chế bởi kiểm duyệt.");
            VideoRules.ValidateVisibility(newVisibility);
            EnsureVisibilityAvailable(newVisibility);
            video.Visibility = newVisibility;

            if (features.ModerationEnabled && newVisibility == "public" && oldVisibility != "public")
            {
                if (video.ModerationStatus != "approved")
                {
                    video.ModerationStatus = "pending";
                    var riskLevel = ModerationRiskLevel(video.Title, video.Description);

                    var hasPendingCase = await db.ModerationCases.AnyAsync(c => c.VideoId == video.VideoId && (c.Status == "pending" || c.Status == "reviewing"), ct);
                    if (!hasPendingCase)
                    {
                        db.ModerationCases.Add(new ModerationCase
                        {
                            VideoId = video.VideoId,
                            Status = "pending",
                            CaseType = "upload_review",
                            RiskLevel = riskLevel,
                            SubmittedAt = Now,
                            UpdatedAt = Now
                        });
                    }
                }
            }
        }
        if (request.ClearCategory) video.CategoryId = null;
        else if (request.CategoryId.HasValue)
        {
            if (!await db.Categories.AnyAsync(x => x.CategoryId == request.CategoryId && x.Status == "active", ct)) throw Error(400, "INVALID_CATEGORY", "Danh mục không hợp lệ.");
            video.CategoryId = request.CategoryId;
        }
        if (request.LanguageCode != null) video.LanguageCode = Clean(request.LanguageCode);
        if (request.AgeRestricted.HasValue) video.AgeRestricted = request.AgeRestricted.Value || video.ModerationAgeRestricted;
        if (request.PromotionEnabled == true)
        {
            if (!await HasVideoPromotionEntitlementAsync(video.UploadedByUserId, ct))
                throw Error(403, "VIDEO_PROMOTION_DENIED", "Gói hiện tại không hỗ trợ quảng bá video.");
            video.PromotionEnabled = true;
        }
        else if (request.PromotionEnabled == false)
        {
            video.PromotionEnabled = false;
        }
        if (request.ThumbnailUrl != null) video.ThumbnailUrl = Clean(request.ThumbnailUrl);
        if (request.Chapters != null)
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(video.Metadata) ?? [];
                metadata["chapters"] = VideoRules.ValidateChapters(request.Chapters.Select(x => new VideoChapter(x.StartSeconds, x.Title)), video.Duration);
                video.Metadata = PersistenceJson.Serialize(metadata);
            }
            catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
        }
        if (request.VideoCards != null)
        {
            var videoCards = await ValidateVideoCardsAsync(video.ChannelId, request.VideoCards, video.Duration, ct);
            try
            {
                var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(video.Metadata) ?? [];
                metadata["videoCards"] = videoCards;
                video.Metadata = PersistenceJson.Serialize(metadata);
            }
            catch (JsonException) { throw Error(400, "INVALID_VIDEO_CARDS", "Không thể lưu video liên quan."); }
        }
        if (request.Tags != null) await ReplaceTagsAsync(video.VideoId, NormalizeTags(request.Tags), ct);
        video.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await ToResponseAsync(video, actorId, ct);
    }

    public async Task<VideoResponse> UpdateThumbnailAsync(Guid actorId, Guid videoId, UpdateThumbnailRequest request, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct);
        await RequireChannelPermissionAsync(video.ChannelId, actorId, ChannelPermissions.VideoEdit, ct);

        string? newThumbnailPath = null;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "hutube-thumbnail-processing", Guid.NewGuid().ToString("N"));
        var persisted = false;
        try
        {
            if (request.Content != null && !string.IsNullOrWhiteSpace(request.FileName) && !string.IsNullOrWhiteSpace(request.ContentType))
            {
                newThumbnailPath = await storage.SaveFileAsync("video-thumbnails", request.FileName, request.Content, request.ContentType, ct);
            }
            else if (request.Generate)
            {
                if (string.IsNullOrWhiteSpace(video.VideoUrl))
                    throw Error(409, "VIDEO_SOURCE_NOT_FOUND", "Không tìm thấy file source của video để tạo thumbnail.");

                Directory.CreateDirectory(temporaryDirectory);
                var sourceFile = Path.Combine(temporaryDirectory, "source.mp4");
                var sourceUrl = await storage.GetReadUrlAsync(video.VideoUrl, TimeSpan.FromMinutes(15), ct);
                using var response = await httpClientFactory.CreateClient().GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw Error(502, "VIDEO_SOURCE_UNAVAILABLE", "Không thể đọc source video để tạo thumbnail.");
                await using (var source = File.Create(sourceFile))
                    await response.Content.CopyToAsync(source, ct);

                var generated = await transcoder.CreateThumbnailAsync(sourceFile, temporaryDirectory, ct);
                if (string.IsNullOrWhiteSpace(generated) || !File.Exists(generated))
                    throw Error(422, "THUMBNAIL_GENERATION_FAILED", "FFmpeg không thể tạo thumbnail từ video này.");

                await using var thumbnail = File.OpenRead(generated);
                newThumbnailPath = await storage.SaveFileAsync(
                    "video-thumbnails", $"{Guid.NewGuid():N}.jpg", thumbnail, "image/jpeg", ct);
            }
            else
            {
                throw Error(400, "THUMBNAIL_REQUIRED", "Vui lòng chọn thumbnail hoặc bật tự động tạo thumbnail.");
            }

            var previousThumbnailPath = video.ThumbnailUrl;
            video.ThumbnailUrl = newThumbnailPath;
            video.UpdatedAt = Now;
            await db.SaveChangesAsync(ct);
            persisted = true;

            if (!string.IsNullOrWhiteSpace(previousThumbnailPath) && !string.Equals(previousThumbnailPath, newThumbnailPath, StringComparison.Ordinal))
            {
                try { await storage.DeleteFileAsync(previousThumbnailPath, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Không thể xóa thumbnail cũ của video {VideoId}", videoId); }
            }

            return await ToResponseAsync(video, actorId, ct);
        }
        catch
        {
            if (!persisted && !string.IsNullOrWhiteSpace(newThumbnailPath))
            {
                try { await storage.DeleteFileAsync(newThumbnailPath, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Không thể dọn thumbnail tạm của video {VideoId}", videoId); }
            }
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
            }
            catch (IOException ex) { logger.LogWarning(ex, "Không thể xóa thư mục thumbnail tạm {Directory}", temporaryDirectory); }
        }
    }

    public async Task DeleteVideoAsync(Guid actorId, Guid videoId, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct);
        var channel = await RequireChannelPermissionAsync(video.ChannelId, actorId, ChannelPermissions.VideoDelete, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockStorageAsync($"plan-membership:{channel.OwnerUserId}", ct);
        await db.Entry(video).ReloadAsync(ct);
        var billingOwnerUserId = video.StorageBillingOwnerUserId ?? await GetStorageBillingOwnerAsync(channel.OwnerUserId, ct);
        await LockStorageAsync($"plan-storage:{billingOwnerUserId}", ct);
        await LockStorageAsync($"quota:{channel.ChannelId}", ct);
        video.Status = "deleted"; video.UpdatedAt = Now; video.MediaRetentionUntil ??= Now.AddDays(30);
        await db.SaveChangesAsync(ct);
        var activeStorage = await GetChannelActiveVideoStorageAsync(channel.ChannelId, ct);
        await db.ChannelQuotas.Where(x => x.ChannelId == channel.ChannelId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.StorageUsed, activeStorage).SetProperty(x => x.UpdatedAt, Now), ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<VideoResponse> SubmitModerationAsync(Guid actorId, Guid videoId, CancellationToken ct = default)
    {
        if (!features.ModerationEnabled) throw Error(409, "MODERATION_UNAVAILABLE", "Kiểm duyệt đang tạm khóa; video chưa thể đặt ở chế độ công khai.");
        var video = await RequireVideoAsync(videoId, ct);
        await RequireChannelPermissionAsync(video.ChannelId, actorId, ChannelPermissions.VideoEdit, ct);
        if (video.ModerationHidden || video.MediaPurgeStartedAt.HasValue || video.MediaPurgedAt.HasValue)
            throw Error(403, "MODERATION_RESTRICTION", "Video đang bị hạn chế bởi kiểm duyệt hoặc đã được dọn media.");
        if (video.ModerationStatus == "approved") throw Error(409, "INVALID_MODERATION_STATE", "Video đã được duyệt.");
        if (video.ModerationStatus == "pending"
            && await db.ModerationCases.AnyAsync(c => c.VideoId == video.VideoId && (c.Status == "pending" || c.Status == "reviewing"), ct))
            return await ToResponseAsync(video, actorId, ct);

        video.ModerationStatus = "pending";
        var now = Now;
        video.UpdatedAt = now;
        db.ModerationCases.Add(CreateUploadModerationCase(video, now));
        await db.SaveChangesAsync(ct);
        return await ToResponseAsync(video, actorId, ct);
    }

    public async Task<VideoResponse> PublishAsync(Guid actorId, Guid videoId, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct);
        await RequireChannelPermissionAsync(video.ChannelId, actorId, ChannelPermissions.VideoEdit, ct);
        if (video.ModerationHidden || video.MediaPurgeStartedAt.HasValue || video.MediaPurgedAt.HasValue)
            throw Error(403, "MODERATION_RESTRICTION", "Video đang bị hạn chế bởi kiểm duyệt hoặc đã được dọn media.");
        if (!features.ModerationEnabled && video.Visibility == "public")
            throw Error(409, "PUBLICATION_LOCKED", "Chế độ công khai đang tạm khóa cho đến khi kiểm duyệt được bật.");
        if (features.ModerationEnabled && video.ModerationStatus != "approved") throw Error(409, "VIDEO_NOT_APPROVED", "Video phải được kiểm duyệt trước khi xuất bản.");
        var wasPublished = video.Status == "published" && video.PublishedAt.HasValue;
        video.Status = "published"; video.PublishedAt ??= Now; video.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        if (!wasPublished)
        {
            await notifications.PublishAsync(actorId, "video_published", "Video đã được xuất bản", video.Title, $"/watch/{video.VideoId}", "video", video.VideoId, ct);

            if (video.Visibility == "public")
            {
                var channelName = await db.Channels.AsNoTracking()
                    .Where(channel => channel.ChannelId == video.ChannelId)
                    .Select(channel => channel.Name)
                    .SingleOrDefaultAsync(ct) ?? "Kênh bạn đăng ký";
                var subscriberIds = await db.Subscriptions.AsNoTracking()
                    .Where(subscription => subscription.ChannelId == video.ChannelId
                        && subscription.Status == "active"
                        && subscription.NotificationsEnabled)
                    .Select(subscription => subscription.UserId)
                    .ToListAsync(ct);

                foreach (var subscriberId in subscriberIds)
                {
                    try
                    {
                        await notifications.PublishInAppAsync(
                            subscriberId,
                            "new_video",
                            $"{channelName} vừa đăng video mới",
                            video.Title,
                            $"/watch/{video.VideoId}",
                            "video",
                            video.VideoId,
                            ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Không thể gửi thông báo video mới tới {UserId} cho video {VideoId}", subscriberId, video.VideoId);
                    }
                }
            }
        }
        return await ToResponseAsync(video, actorId, ct);
    }

    public async Task CancelUploadAsync(Guid actorId, Guid videoId, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct);
        await RequireChannelPermissionAsync(video.ChannelId, actorId, ChannelPermissions.VideoDelete, ct);
        if (video.Status == "published") throw Error(409, "PUBLISHED_VIDEO_CANNOT_CANCEL", "Video đã xuất bản không thể hủy upload.");
        await DeleteVideoAsync(actorId, videoId, ct);
    }

    public async Task<VideoResponse> RetryProcessingAsync(Guid actorId, Guid videoId, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct);
        await RequireChannelPermissionAsync(video.ChannelId, actorId, ChannelPermissions.VideoEdit, ct);
        if (video.ModerationHidden || video.MediaPurgeStartedAt.HasValue || video.MediaPurgedAt.HasValue)
            throw Error(403, "MODERATION_RESTRICTION", "Video đang bị hạn chế bởi kiểm duyệt hoặc đã được dọn media.");
        if (video.Status != "failed") throw Error(409, "VIDEO_NOT_RETRYABLE", "Chỉ video xử lý lỗi mới có thể thử lại.");
        if (video.MediaPurgedAt.HasValue) throw Error(409, "VIDEO_MEDIA_PURGED", "Tệp video đã được dọn và không thể xử lý lại.");
        var sourceRendition = await db.VideoRenditions
            .Where(x => x.VideoId == videoId && x.Codec == "source")
            .OrderByDescending(x => x.Height)
            .FirstOrDefaultAsync(ct)
            ?? throw Error(409, "VIDEO_SOURCE_RENDITION_NOT_FOUND", "Không tìm thấy source để encode lại video.");
        // The source file is the input for every retry and must not be retried
        // as a derived rendition. Older failures could have marked all rows as
        // failed, so restore the source row to ready when its file still exists.
        var sourceQuality = sourceRendition.QualityLabel;
        sourceRendition.Status = "ready";
        sourceRendition.UpdatedAt = Now;
        video.Status = "processing"; video.ModerationStatus = "not_submitted"; video.UpdatedAt = Now;
        var renditions = await db.VideoRenditions
            .Where(x => x.VideoId == videoId && x.Status == "failed" && x.Codec != "source")
            .ToListAsync(ct);
        foreach (var rendition in renditions) { rendition.Status = "processing"; rendition.UpdatedAt = Now; }
        await db.SaveChangesAsync(ct);
        renditionQueue.Enqueue(new DeferredVideoRenditionJob(videoId, sourceQuality));
        return await ToResponseAsync(video, actorId, ct);
    }

    public async Task<WatchProgressResponse> SaveProgressAsync(Guid userId, Guid videoId, WatchProgressRequest request, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct); await EnsureCanViewAsync(video, userId, ct);
        decimal progress;
        try { progress = VideoRules.CalculateProgress(request.WatchedSeconds, video.Duration); }
        catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
        if (!request.SaveHistory) return new(Math.Clamp(request.WatchedSeconds, 0, video.Duration), progress, Now);
        var item = await db.ViewingHistories.Where(x => x.UserId == userId && x.VideoId == videoId).OrderByDescending(x => x.ViewedAt).FirstOrDefaultAsync(ct);
        var now = Now;
        if (item == null || request.NewSession || (now - item.ViewedAt) > ViewSessionCooldown)
        {
            item = new ViewingHistory { UserId = userId, VideoId = videoId };
            db.ViewingHistories.Add(item);
            db.ChannelAnalyticsEvents.Add(new() { ChannelId = video.ChannelId, VideoId = videoId, OccurredAt = now, Metric = "views", Value = 1 });
        }
        else
        {
            // Progress is a playback position, not elapsed time. Bound the
            // estimate by wall time and discard long gaps rather than treating
            // a seek or an idle browser as a long viewing session.
            var elapsed = (now - item.ViewedAt).TotalSeconds;
            var advance = Math.Clamp(request.WatchedSeconds, 0, video.Duration) - item.WatchDuration;
            var seconds = elapsed is > 0 and <= 30 && advance > 0
                ? Math.Min(advance, (int)Math.Ceiling(elapsed * 2)) : 0;
            if (seconds > 0)
                db.ChannelAnalyticsEvents.Add(new() { ChannelId = video.ChannelId, VideoId = videoId, OccurredAt = now, Metric = "watchSeconds", Value = seconds });
        }
        item.WatchDuration = Math.Clamp(request.WatchedSeconds, 0, video.Duration); item.Progress = progress; item.ViewedAt = now;
        await db.SaveChangesAsync(ct);
        return new(item.WatchDuration, item.Progress, item.ViewedAt);
    }

    public async Task<ReactionResponse> SetVideoReactionAsync(Guid userId, Guid videoId, string? reaction, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct); await EnsureCanViewAsync(video, userId, ct);
        var item = await db.VideoReactions.SingleOrDefaultAsync(x => x.UserId == userId && x.VideoId == videoId, ct);
        var previousReaction = item?.Type;
        var normalizedReaction = reaction?.ToLowerInvariant();
        if (reaction == null) { if (item != null) db.VideoReactions.Remove(item); }
        else
        {
            try { VideoRules.ValidateReaction(reaction); } catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
            if (item == null) { item = new VideoReaction { UserId = userId, VideoId = videoId, CreatedAt = Now }; db.VideoReactions.Add(item); }
            item.Type = normalizedReaction!; item.UpdatedAt = Now;
        }
        if (normalizedReaction == "like" && previousReaction != "like")
            db.ChannelAnalyticsEvents.Add(new() { ChannelId = video.ChannelId, VideoId = videoId, OccurredAt = Now, Metric = "likes", Value = 1 });
        await db.SaveChangesAsync(ct);
        if (normalizedReaction == "like" && previousReaction != "like")
        {
            var ownerId = await db.Channels.AsNoTracking().Where(x => x.ChannelId == video.ChannelId).Select(x => x.OwnerUserId).SingleAsync(ct);
            await PublishInteractionNotificationAsync(ownerId, userId, "video_like", "Video của bạn vừa được thích",
                video.Title, $"/watch/{videoId}", "video", videoId, ct);
        }
        else if (normalizedReaction == "dislike" && previousReaction != "dislike")
        {
            var ownerId = await db.Channels.AsNoTracking().Where(x => x.ChannelId == video.ChannelId).Select(x => x.OwnerUserId).SingleAsync(ct);
            await PublishInteractionNotificationAsync(ownerId, userId, "video_dislike", "Video của bạn có lượt không thích mới",
                video.Title, $"/watch/{videoId}", "video", videoId, ct);
        }
        var reactionCounts = await db.VideoReactions.AsNoTracking().Where(x => x.VideoId == videoId)
            .GroupBy(x => x.Type).Select(group => new { group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, ct);
        return new(normalizedReaction, reactionCounts.GetValueOrDefault("like"), reactionCounts.GetValueOrDefault("dislike"));
    }

    public async Task<RatingResponse> SetRatingAsync(Guid userId, Guid videoId, int? score, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct); await EnsureCanViewAsync(video, userId, ct);
        var item = await db.VideoRatings.SingleOrDefaultAsync(x => x.UserId == userId && x.VideoId == videoId, ct);
        if (!score.HasValue) { if (item != null) db.VideoRatings.Remove(item); }
        else
        {
            try { VideoRules.ValidateRating(score.Value); } catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
            if (item == null) { item = new VideoRating { UserId = userId, VideoId = videoId, EvaluatedAt = Now }; db.VideoRatings.Add(item); }
            item.Score = (short)score.Value; item.UpdatedAt = Now;
        }
        await db.SaveChangesAsync(ct);
        var aggregate = await db.VideoRatings.AsNoTracking().Where(x => x.VideoId == videoId)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Average = group.Average(row => (double)row.Score) })
            .SingleOrDefaultAsync(ct);
        return new(score, aggregate == null ? null : Math.Round((decimal)aggregate.Average, 2), aggregate?.Count ?? 0);
    }

    public async Task<ShareResponse> ShareAsync(Guid userId, Guid videoId, string? method, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct); await EnsureCanViewAsync(video, userId, ct);
        db.ShareHistories.Add(new ShareHistory { UserId = userId, VideoId = videoId, ShareMethod = Clean(method) ?? "copy_link", SharedAt = Now });
        await db.SaveChangesAsync(ct);
        return new($"/watch/{videoId}", await db.ShareHistories.LongCountAsync(x => x.VideoId == videoId, ct));
    }

    public async Task<PageResult<CommentResponse>> GetCommentsAsync(Guid videoId, Guid? viewerId, string sort, int page, int pageSize, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct); await EnsureCanViewAsync(video, viewerId, ct); (page, pageSize) = Page(page, pageSize);
        var query = db.Comments.AsNoTracking().Where(x => x.VideoId == videoId && x.ParentCommentId == null && x.Status == "visible");
        var total = await query.CountAsync(ct);
        List<Comment> comments;
        if (sort.Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            var likeCounts = db.CommentReactions.AsNoTracking().Where(reaction => reaction.Type == "like")
                .GroupBy(reaction => reaction.CommentId)
                .Select(group => new { CommentId = group.Key, Count = group.LongCount() });
            var replyCounts = db.Comments.AsNoTracking()
                .Where(reply => reply.ParentCommentId.HasValue && reply.Status == "visible")
                .GroupBy(reply => reply.ParentCommentId!.Value)
                .Select(group => new { CommentId = group.Key, Count = group.LongCount() });
            comments = await (from comment in query
                              join likes in likeCounts on comment.CommentId equals likes.CommentId into likeRows
                              from likes in likeRows.DefaultIfEmpty()
                              join replies in replyCounts on comment.CommentId equals replies.CommentId into replyRows
                              from replies in replyRows.DefaultIfEmpty()
                              let likeCount = (long?)likes.Count
                              let replyCount = (long?)replies.Count
                              orderby comment.IsPinned descending, ((likeCount ?? 0L) + (replyCount ?? 0L)) descending, comment.CreatedAt descending, comment.CommentId descending
                              select comment)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        }
        else
        {
            comments = await query.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.CommentId)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        }
        var items = await ToCommentsAsync(comments, viewerId, ct);
        return new(items, page, pageSize, total);
    }

    public async Task<PageResult<CommentResponse>> GetRepliesAsync(Guid commentId, Guid? viewerId, int page, int pageSize, CancellationToken ct = default)
    {
        var parent = await RequireCommentAsync(commentId, ct); var video = await RequireVideoAsync(parent.VideoId, ct);
        await EnsureCanViewAsync(video, viewerId, ct); (page, pageSize) = Page(page, pageSize);
        var query = db.Comments.AsNoTracking().Where(x => x.ParentCommentId == commentId && x.Status == "visible");
        var total = await query.CountAsync(ct); var replies = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.CommentId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = await ToCommentsAsync(replies, viewerId, ct);
        return new(items, page, pageSize, total);
    }

    public async Task<PageResult<CommentResponse>> GetManagedCommentsAsync(Guid actorId, Guid channelId, string? status, string? sort, int page, int pageSize, CancellationToken ct = default)
    {
        await RequireChannelPermissionAsync(channelId, actorId, ChannelPermissions.CommentManage, ct);
        (page, pageSize) = Page(page, pageSize);
        var query = db.Comments.AsNoTracking().Where(x => db.Videos.Any(v => v.VideoId == x.VideoId && v.ChannelId == channelId) && x.Status != "deleted");
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.ToLowerInvariant());
        var total = await query.CountAsync(ct);
        var normalizedSort = sort?.Trim().ToLowerInvariant();
        List<Comment> rows;
        if (normalizedSort is "mostliked" or "most_liked" or "top" or "likes")
        {
            var channelCommentIds = query.Select(comment => comment.CommentId);
            var likeCounts = db.CommentReactions.AsNoTracking()
                .Where(reaction => reaction.Type == "like" && channelCommentIds.Contains(reaction.CommentId))
                .GroupBy(reaction => reaction.CommentId)
                .Select(group => new { CommentId = group.Key, Count = group.LongCount() });
            rows = await (from comment in query
                          join likes in likeCounts on comment.CommentId equals likes.CommentId into likeRows
                          from likes in likeRows.DefaultIfEmpty()
                          orderby ((long?)likes.Count ?? 0L) descending, comment.CreatedAt descending, comment.CommentId descending
                          select comment)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        }
        else if (normalizedSort == "oldest")
        {
            rows = await query.OrderBy(comment => comment.CreatedAt).ThenBy(comment => comment.CommentId)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        }
        else
        {
            rows = await query.OrderByDescending(comment => comment.CreatedAt).ThenByDescending(comment => comment.CommentId)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        }
        var items = await ToCommentsAsync(rows, actorId, ct);
        return new(items, page, pageSize, total);
    }

    public async Task<IReadOnlyList<ViolationTypeResponse>> GetViolationTypesAsync(CancellationToken ct = default)
    {
        var list = await db.ViolationTypes.AsNoTracking().Where(x => x.Status == "active").OrderBy(x => x.Name)
            .Select(x => new ViolationTypeResponse(x.ViolationTypeId, x.Code, x.Name, x.Description)).ToListAsync(ct);
        if (list.Count > 0) return list;

        var defaults = new List<ViolationType>
        {
            new() { ViolationTypeId = Guid.Parse("00000000-0000-0002-0000-000000000001"), Code = "sexual", Name = "Nội dung khiêu dâm", Description = "Hình ảnh, video hoặc nội dung khiêu dâm, không phù hợp thuần phong mỹ tục.", Status = "active", CreatedAt = Now, UpdatedAt = Now },
            new() { ViolationTypeId = Guid.Parse("00000000-0000-0002-0000-000000000002"), Code = "violent", Name = "Nội dung bạo lực hoặc phản cảm", Description = "Bạo lực, đẫm máu, gây sốc hoặc phản cảm.", Status = "active", CreatedAt = Now, UpdatedAt = Now },
            new() { ViolationTypeId = Guid.Parse("00000000-0000-0002-0000-000000000003"), Code = "hate", Name = "Nội dung lăng mạ hoặc kích động thù hận", Description = "Xúc phạm danh dự, kỳ thị hoặc kích động thù địch.", Status = "active", CreatedAt = Now, UpdatedAt = Now },
            new() { ViolationTypeId = Guid.Parse("00000000-0000-0002-0000-000000000004"), Code = "harassment", Name = "Nội dung quấy rối hoặc bắt nạt", Description = "Đe dọa, quấy rối, bắt nạt trực tuyến.", Status = "active", CreatedAt = Now, UpdatedAt = Now },
            new() { ViolationTypeId = Guid.Parse("00000000-0000-0002-0000-000000000005"), Code = "harmful", Name = "Hành động gây hại hoặc nguy hiểm", Description = "Hành vi khuyến khích nguy hiểm hoặc tự gây hại.", Status = "active", CreatedAt = Now, UpdatedAt = Now },
            new() { ViolationTypeId = Guid.Parse("00000000-0000-0002-0000-000000000006"), Code = "spam", Name = "Spam hoặc thông tin sai lệch", Description = "Lừa đảo, tin giả, quảng cáo rác hoặc thao túng người xem.", Status = "active", CreatedAt = Now, UpdatedAt = Now },
            new() { ViolationTypeId = Guid.Parse("00000000-0000-0002-0000-000000000007"), Code = "copyright", Name = "Vi phạm bản quyền", Description = "Sử dụng tác phẩm không có bản quyền hoặc quyền sở hữu hợp pháp.", Status = "active", CreatedAt = Now, UpdatedAt = Now },
            new() { ViolationTypeId = Guid.Parse("00000000-0000-0002-0000-000000000008"), Code = "other", Name = "Vi phạm khác", Description = "Các hành vi vi phạm điều khoản dịch vụ hoặc tiêu chuẩn cộng đồng khác.", Status = "active", CreatedAt = Now, UpdatedAt = Now }
        };
        try
        {
            db.ViolationTypes.AddRange(defaults);
            await db.SaveChangesAsync(ct);
        }
        catch { /* ignore duplicate key if race condition */ }
        return defaults.Select(x => new ViolationTypeResponse(x.ViolationTypeId, x.Code, x.Name, x.Description)).ToList();
    }

    public async Task<CommentResponse> CreateCommentAsync(Guid userId, Guid videoId, CreateCommentRequest request, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct); await EnsureCanViewAsync(video, userId, ct);
        if (!video.AllowComments) throw Error(403, "COMMENTS_DISABLED", "Video đã tắt bình luận.");
        string content; try { content = VideoRules.ValidateComment(request.Content); } catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
        Comment? parent = null;
        if (request.ParentCommentId.HasValue)
        {
            parent = await db.Comments.SingleOrDefaultAsync(x => x.CommentId == request.ParentCommentId && x.VideoId == videoId && x.Status != "deleted", ct)
                ?? throw Error(404, "PARENT_COMMENT_NOT_FOUND", "Không tìm thấy bình luận cha.");
        }
        var comment = new Comment { UserId = userId, VideoId = videoId, ParentCommentId = parent?.CommentId, Content = content, Status = "visible", CreatedAt = Now, UpdatedAt = Now };
        if (await db.Comments.CountAsync(x => x.UserId == userId && x.VideoId == videoId && x.Content == content && x.CreatedAt > Now.AddSeconds(-10), ct) >= 2)
            throw Error(429, "COMMENT_SPAM_DETECTED", "Bạn đang gửi cùng một bình luận quá nhanh.");
        db.Comments.Add(comment); await db.SaveChangesAsync(ct);
        if (parent != null && parent.UserId != userId)
        {
            await PublishInteractionNotificationAsync(parent.UserId, userId, "comment_reply", "Có người trả lời bình luận", content,
                $"/watch/{videoId}?comment={comment.CommentId}", "comment", parent.CommentId, ct);
        }
        var ownerId = await db.Channels.AsNoTracking().Where(x => x.ChannelId == video.ChannelId).Select(x => x.OwnerUserId).SingleOrDefaultAsync(ct);
        if (ownerId != Guid.Empty && ownerId != userId && (parent == null || parent.UserId != ownerId))
        {
            var commentTitle = parent == null
                ? "Bình luận mới về video của bạn"
                : "Phản hồi mới trong video của bạn";
            await PublishInteractionNotificationAsync(ownerId, userId, "video_comment", commentTitle,
                $"\"{content}\" trên video '{video.Title}'", $"/watch/{videoId}?comment={comment.CommentId}", "video", videoId, ct);
        }
        return await ToCommentAsync(comment, userId, ct);
    }

    public async Task<CommentResponse> UpdateCommentAsync(Guid userId, Guid commentId, UpdateCommentRequest request, CancellationToken ct = default)
    {
        var comment = await RequireCommentAsync(commentId, ct);
        if (comment.UserId != userId) throw Error(403, "COMMENT_EDIT_DENIED", "Bạn chỉ có thể sửa bình luận của mình.");
        try { comment.Content = VideoRules.ValidateComment(request.Content); } catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
        comment.UpdatedAt = Now; await db.SaveChangesAsync(ct); return await ToCommentAsync(comment, userId, ct);
    }

    public async Task DeleteCommentAsync(Guid userId, Guid commentId, CancellationToken ct = default)
    {
        var comment = await RequireCommentAsync(commentId, ct);
        var video = await RequireVideoAsync(comment.VideoId, ct);
        if (comment.UserId != userId && !await HasChannelPermissionAsync(video.ChannelId, userId, ChannelPermissions.CommentManage, ct))
            throw Error(403, "COMMENT_DELETE_DENIED", "Bạn không có quyền xóa bình luận này.");
        comment.Status = "deleted"; comment.IsPinned = false; comment.Content = "[đã xóa]"; comment.UpdatedAt = Now;
        db.Entry(comment).Property(x => x.IsPinned).IsModified = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ReactionResponse> SetCommentReactionAsync(Guid userId, Guid commentId, string? reaction, CancellationToken ct = default)
    {
        var comment = await RequireCommentAsync(commentId, ct);
        var video = await RequireVideoAsync(comment.VideoId, ct); await EnsureCanViewAsync(video, userId, ct);
        var item = await db.CommentReactions.SingleOrDefaultAsync(x => x.UserId == userId && x.CommentId == commentId, ct);
        var previousReaction = item?.Type;
        var normalizedReaction = reaction?.ToLowerInvariant();
        if (reaction == null) { if (item != null) db.CommentReactions.Remove(item); }
        else
        {
            try { VideoRules.ValidateReaction(reaction); } catch (VideoValidationException ex) { throw Error(400, ex.Code, ex.Message); }
            if (item == null) { item = new CommentReaction { UserId = userId, CommentId = commentId, CreatedAt = Now }; db.CommentReactions.Add(item); }
            item.Type = normalizedReaction!; item.UpdatedAt = Now;
        }
        await db.SaveChangesAsync(ct);
        if (normalizedReaction == "like" && previousReaction != "like")
        {
            await PublishInteractionNotificationAsync(comment.UserId, userId, "comment_like", "Bình luận của bạn vừa được thích",
                video.Title, $"/watch/{video.VideoId}?comment={comment.CommentId}", "comment", comment.CommentId, ct);
        }
        return new(normalizedReaction, await db.CommentReactions.LongCountAsync(x => x.CommentId == commentId && x.Type == "like", ct), await db.CommentReactions.LongCountAsync(x => x.CommentId == commentId && x.Type == "dislike", ct));
    }

    public async Task<CommentResponse> SetCommentCreatorStateAsync(Guid actorId, Guid commentId, bool enabled, bool pin, CancellationToken ct = default)
    {
        var comment = await RequireCommentAsync(commentId, ct);
        var video = await RequireVideoAsync(comment.VideoId, ct);
        await RequireChannelPermissionAsync(video.ChannelId, actorId, ChannelPermissions.CommentManage, ct);
        if (enabled && (comment.Status != "visible" || (pin && comment.ParentCommentId.HasValue)))
            throw Error(400, "COMMENT_CREATOR_STATE_INVALID", "Chỉ ghim bình luận gốc và tương tác với bình luận đang hiển thị.");
        if (pin)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Serialize pin changes for the video so concurrent requests cannot leave two pins.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.videos WHERE video_id = {comment.VideoId} FOR UPDATE", ct);
            if (enabled)
                await db.Comments.Where(x => x.VideoId == comment.VideoId && x.CommentId != commentId && x.IsPinned)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsPinned, false), ct);
            var candidates = db.Comments.Where(x => x.CommentId == commentId && x.Status != "deleted");
            if (enabled) candidates = candidates.Where(x => x.Status == "visible" && x.ParentCommentId == null);
            var changed = await candidates.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.IsPinned, enabled).SetProperty(x => x.UpdatedAt, Now), ct);
            if (changed == 0) throw Error(400, "COMMENT_CREATOR_STATE_INVALID", "Bình luận đã thay đổi. Vui lòng tải lại.");
            await transaction.CommitAsync(ct);
            await db.Entry(comment).ReloadAsync(ct);
        }
        else
        {
            comment.HasCreatorHeart = enabled; comment.UpdatedAt = Now;
            await db.SaveChangesAsync(ct);
        }
        return await ToCommentAsync(comment, actorId, ct);
    }

    public async Task<CommentResponse> SetCommentHiddenAsync(Guid actorId, Guid commentId, bool hidden, string? reason, CancellationToken ct = default)
    {
        var comment = await RequireCommentAsync(commentId, ct); var video = await RequireVideoAsync(comment.VideoId, ct);
        await RequireChannelPermissionAsync(video.ChannelId, actorId, ChannelPermissions.CommentManage, ct);
        comment.Status = hidden ? "hidden" : "visible"; comment.IsPinned = false; comment.UpdatedAt = Now;
        db.Entry(comment).Property(x => x.IsPinned).IsModified = true;
        db.CommentModerationActions.Add(new CommentModerationAction { CommentId = commentId, ModeratorUserId = actorId, ActionType = hidden ? "hide" : "unhide", Reason = Clean(reason), CreatedAt = Now });
        await db.SaveChangesAsync(ct); return await ToCommentAsync(comment, actorId, ct);
    }

    public async Task<IReadOnlyList<RenditionResponse>> GetDownloadOptionsAsync(Guid userId, Guid videoId, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct); await EnsureCanViewAsync(video, userId, ct);
        await EnsureDownloadAllowedAsync(userId, ct);
        var maxHeight = await MaxDownloadHeightAsync(userId, ct);
        var rows = await db.VideoRenditions.AsNoTracking().Where(x => x.VideoId == videoId && x.Status == "ready" && x.Height <= maxHeight).OrderBy(x => x.Height).ToListAsync(ct);
        var result = new List<RenditionResponse>();
        foreach (var row in rows) result.Add(new(row.QualityLabel, row.Width, row.Height, row.FileSize, await ReadUrlAsync(row.FileUrl, ct), DownloadExtension(row.FileUrl)));
        return result;
    }

    public async Task<DownloadResponse> CreateDownloadAsync(Guid userId, Guid videoId, string quality, CancellationToken ct = default)
    {
        var video = await RequireVideoAsync(videoId, ct); await EnsureCanViewAsync(video, userId, ct);
        await EnsureDownloadAllowedAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(quality)) throw Error(400, "INVALID_DOWNLOAD_QUALITY", "Vui lòng chọn chất lượng tải xuống.");
        var normalizedQuality = quality.Trim().ToLowerInvariant();
        var maxHeight = await MaxDownloadHeightAsync(userId, ct);
        var selected = await db.VideoRenditions.AsNoTracking().SingleOrDefaultAsync(x => x.VideoId == videoId && x.Status == "ready"
            && x.QualityLabel == normalizedQuality && x.Height <= maxHeight, ct)
            ?? throw Error(403, "DOWNLOAD_QUALITY_DENIED", "Gói hiện tại không hỗ trợ chất lượng tải xuống này.");
        var item = await db.VideoDownloads.SingleOrDefaultAsync(x => x.UserId == userId && x.VideoId == videoId && x.QualityLabel == selected.QualityLabel, ct);
        if (item == null)
        {
            item = new VideoDownload { UserId = userId, VideoId = videoId, QualityLabel = selected.QualityLabel, FileUrl = selected.FileUrl, FileSize = selected.FileSize, Status = "ready", CreatedAt = Now, UpdatedAt = Now };
            db.VideoDownloads.Add(item); await db.SaveChangesAsync(ct);
        }
        else
        {
            item.FileUrl = selected.FileUrl;
            item.FileSize = selected.FileSize;
            item.Status = "ready";
            item.UpdatedAt = Now;
            await db.SaveChangesAsync(ct);
        }
        return new(item.VideoDownloadId, videoId, video.Title, item.QualityLabel, await ReadUrlAsync(item.FileUrl, ct), item.FileSize, item.Status, item.CreatedAt);
    }

    public async Task<DownloadFileResponse> OpenDownloadFileAsync(Guid userId, Guid videoId, string quality, CancellationToken ct = default)
    {
        // Reuse the same visibility, plan and rendition checks as download creation.
        var download = await CreateDownloadAsync(userId, videoId, quality, ct);
        var storedPath = await db.VideoDownloads.AsNoTracking()
            .Where(x => x.VideoDownloadId == download.VideoDownloadId && x.UserId == userId)
            .Select(x => x.FileUrl).SingleAsync(ct);
        var extension = DownloadExtension(storedPath);
        var contentType = extension switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            _ => "video/mp4"
        };
        var title = new string(download.Title.Where(c => !char.IsControl(c) && !"<>:\"/\\|?*".Contains(c)).Take(120).ToArray()).Trim().TrimEnd('.');
        if (title.Length == 0) title = "Dalitu";
        var stream = await storage.OpenReadAsync(storedPath, ct);
        return new(stream, $"{title}-{download.Quality}{extension}", contentType);
    }

    private static string DownloadExtension(string storedPath)
    {
        var path = Uri.TryCreate(storedPath, UriKind.Absolute, out var uri) ? uri.AbsolutePath : storedPath;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".webm" or ".mov" or ".mkv" ? extension : ".mp4";
    }

    public async Task<IReadOnlyList<DownloadResponse>> GetDownloadsAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await (from item in db.VideoDownloads.AsNoTracking() join video in db.Videos.AsNoTracking() on item.VideoId equals video.VideoId
                          where item.UserId == userId orderby item.CreatedAt descending select new { item, Video = video })
            .Take(200).ToListAsync(ct);
        var result = new List<DownloadResponse>();
        foreach (var row in rows)
        {
            var allowed = await CanIssueDownloadAsync(userId, row.Video, row.item.QualityLabel, ct);
            result.Add(new(row.item.VideoDownloadId, row.item.VideoId, allowed ? row.Video.Title : "Video không khả dụng", row.item.QualityLabel,
                allowed ? await ReadUrlAsync(row.item.FileUrl, ct) : "", row.item.FileSize, allowed ? row.item.Status : "revoked", row.item.CreatedAt));
        }
        return result;
    }

    public async Task DeleteDownloadAsync(Guid userId, Guid downloadId, CancellationToken ct = default)
    {
        var item = await db.VideoDownloads.SingleOrDefaultAsync(x => x.VideoDownloadId == downloadId && x.UserId == userId, ct)
            ?? throw Error(404, "DOWNLOAD_NOT_FOUND", "Không tìm thấy bản tải xuống.");
        db.VideoDownloads.Remove(item); await db.SaveChangesAsync(ct);
    }

    public async Task<DownloadResponse> SetDownloadStateAsync(Guid userId, Guid downloadId, string action, CancellationToken ct = default)
    {
        var item = await db.VideoDownloads.SingleOrDefaultAsync(x => x.VideoDownloadId == downloadId && x.UserId == userId, ct) ?? throw Error(404, "DOWNLOAD_NOT_FOUND", "Không tìm thấy bản tải xuống.");
        var video = await RequireVideoAsync(item.VideoId, ct);
        if (!await CanIssueDownloadAsync(userId, video, item.QualityLabel, ct))
            throw Error(403, "DOWNLOAD_REVOKED", "Quyền tải video đã bị thu hồi.");
        item.Status = action.ToLowerInvariant() switch
        {
            "pause" when item.Status is "pending" or "processing" => "cancelled",
            "cancel" when item.Status is not "cancelled" => "cancelled",
            "resume" when item.Status == "cancelled" => "processing",
            "retry" when item.Status is "failed" or "cancelled" => "processing",
            _ => throw Error(409, "INVALID_DOWNLOAD_STATE", "Không thể chuyển trạng thái tải xuống theo yêu cầu.")
        };
        item.UpdatedAt = Now; await db.SaveChangesAsync(ct);
        var title = await db.Videos.Where(x => x.VideoId == item.VideoId).Select(x => x.Title).SingleAsync(ct);
        return new(item.VideoDownloadId, item.VideoId, title, item.QualityLabel, await ReadUrlAsync(item.FileUrl, ct), item.FileSize, item.Status, item.CreatedAt);
    }

    public async Task DeleteDownloadsAsync(Guid userId, IReadOnlyList<Guid> downloadIds, CancellationToken ct = default)
    {
        if (downloadIds.Count == 0) throw Error(400, "DOWNLOAD_IDS_REQUIRED", "Vui lòng chọn ít nhất một bản tải xuống.");
        var rows = await db.VideoDownloads.Where(x => x.UserId == userId && downloadIds.Contains(x.VideoDownloadId)).ToListAsync(ct);
        db.VideoDownloads.RemoveRange(rows); await db.SaveChangesAsync(ct);
    }

    private async Task<bool> CanIssueDownloadAsync(Guid userId, Video video, string quality, CancellationToken ct)
    {
        try
        {
            await EnsureCanViewAsync(video, userId, ct);
            await EnsureDownloadAllowedAsync(userId, ct);
            return VideoRules.QualityHeight(quality) <= await MaxDownloadHeightAsync(userId, ct);
        }
        catch (ContentException) { return false; }
    }

    private async Task<VideoResponse> ToResponseAsync(Video video, Guid? viewerId, CancellationToken ct)
    {
        var channel = await db.Channels.AsNoTracking().SingleAsync(x => x.ChannelId == video.ChannelId, ct);
        var tags = await (from vt in db.VideoTags.AsNoTracking() join tag in db.Tags.AsNoTracking() on vt.TagId equals tag.TagId where vt.VideoId == video.VideoId orderby tag.Name select tag.Name).ToListAsync(ct);
        var chapters = ReadChapters(video.Metadata);
        // Aggregate on the server in one round trip rather than issuing eight
        // sequential commands for each video detail request under load.
        var stats = await db.Videos.AsNoTracking().Where(row => row.VideoId == video.VideoId)
            .Select(row => new {
                Likes = db.VideoReactions.LongCount(x => x.VideoId == row.VideoId && x.Type == "like"),
                Dislikes = db.VideoReactions.LongCount(x => x.VideoId == row.VideoId && x.Type == "dislike"),
                Views = db.ViewingHistories.LongCount(x => x.VideoId == row.VideoId),
                WatchSeconds = db.ViewingHistories.Where(x => x.VideoId == row.VideoId).Sum(x => (long?)x.WatchDuration) ?? 0,
                Comments = db.Comments.Count(x => x.VideoId == row.VideoId && x.Status == "visible"),
                AverageRating = db.VideoRatings.Where(x => x.VideoId == row.VideoId).Average(x => (double?)x.Score),
                RatingCount = db.VideoRatings.Count(x => x.VideoId == row.VideoId),
                Shares = db.ShareHistories.LongCount(x => x.VideoId == row.VideoId)
            }).SingleAsync(ct);
        var videoCards = await ResolveVideoCardLinksAsync(video, ct);
        VideoViewerStateResponse? state = null;
        if (viewerId.HasValue)
        {
            var reaction = await db.VideoReactions.AsNoTracking().SingleOrDefaultAsync(x => x.VideoId == video.VideoId && x.UserId == viewerId, ct);
            var rating = await db.VideoRatings.AsNoTracking().SingleOrDefaultAsync(x => x.VideoId == video.VideoId && x.UserId == viewerId, ct);
            var history = await db.ViewingHistories.AsNoTracking().Where(x => x.VideoId == video.VideoId && x.UserId == viewerId).OrderByDescending(x => x.ViewedAt).FirstOrDefaultAsync(ct);
            state = new(reaction?.Type, rating?.Score, history?.WatchDuration ?? 0, history?.Progress ?? 0);
        }
        return new(video.VideoId, video.ChannelId, channel.Name, channel.Handle, video.CategoryId, video.Title, video.Description,
            "", video.ThumbnailUrl == null ? null : await ReadUrlAsync(video.ThumbnailUrl, ct), video.Duration, video.FileSize, video.Visibility, video.Status, video.ModerationStatus,
            video.LanguageCode, video.AgeRestricted, video.PublishedAt, video.CreatedAt, tags, chapters,
            new(stats.Views, stats.Likes, stats.Dislikes, stats.Comments,
                stats.AverageRating.HasValue ? Math.Round((decimal)stats.AverageRating.Value, 2) : null,
                stats.RatingCount, stats.Shares, stats.WatchSeconds), state,
             viewerId == channel.OwnerUserId ? video.ModerationReason : null,
             channel.WatermarkUrl == null ? null : await ReadUrlAsync(channel.WatermarkUrl, ct),
             video.PromotionEnabled, videoCards);
    }

    private async Task<IReadOnlyList<VideoCardLinkResponse>> ResolveVideoCardLinksAsync(Video video, CancellationToken ct)
    {
        var references = ReadVideoCardReferences(video.Metadata);
        if (references.Count == 0) return [];
        var ids = references.Select(card => card.VideoId).Distinct().ToArray();
        var targets = await db.Videos.AsNoTracking()
            .Where(target => ids.Contains(target.VideoId) && target.ChannelId == video.ChannelId
                && target.Status == "published" && target.ModerationStatus == "approved" && target.Visibility == "public")
            .Select(target => new { target.VideoId, target.Title, target.ThumbnailUrl })
            .ToListAsync(ct);
        var byId = targets.ToDictionary(target => target.VideoId);
        var result = new List<VideoCardLinkResponse>(references.Count);
        foreach (var reference in references.OrderBy(card => card.StartSeconds))
        {
            if (!byId.TryGetValue(reference.VideoId, out var target)) continue;
            result.Add(new(reference.VideoId, reference.StartSeconds, target.Title,
                target.ThumbnailUrl == null ? null : await ReadUrlAsync(target.ThumbnailUrl, ct)));
        }
        return result;
    }

    private async Task<IReadOnlyList<VideoCardRequest>> ValidateVideoCardsAsync(
        Guid channelId, IReadOnlyList<VideoCardRequest>? cards, int duration, CancellationToken ct)
    {
        var requested = cards ?? [];
        if (requested.Count > 5 || requested.Select(card => card.VideoId).Distinct().Count() != requested.Count
            || requested.Select(card => card.StartSeconds).Distinct().Count() != requested.Count
            || requested.Any(card => card.StartSeconds < 0 || card.StartSeconds >= duration))
            throw Error(400, "INVALID_VIDEO_CARDS", "Tối đa 5 video liên quan, mỗi video chỉ được gắn một lần tại mốc hợp lệ.");
        if (requested.Count == 0) return [];
        var ids = requested.Select(card => card.VideoId).ToArray();
        var eligibleIds = await db.Videos.AsNoTracking()
            .Where(video => ids.Contains(video.VideoId) && video.ChannelId == channelId
                && video.Status == "published" && video.ModerationStatus == "approved" && video.Visibility == "public")
            .Select(video => video.VideoId)
            .ToListAsync(ct);
        if (eligibleIds.Count != requested.Count)
            throw Error(400, "INVALID_VIDEO_CARD_TARGET", "Chỉ có thể gắn video công khai đã xuất bản trong cùng kênh.");
        return requested.OrderBy(card => card.StartSeconds).ToArray();
    }

    private Task<bool> HasVideoPromotionEntitlementAsync(Guid userId, CancellationToken ct)
    {
        var now = Now;
        return db.PlanHistories.AsNoTracking().AnyAsync(history =>
            history.Status == "active"
            && history.StartedAt <= now
            && (!history.EndedAt.HasValue || history.EndedAt > now)
            && (history.UserId == userId || db.PlanMembers.AsNoTracking().Any(member =>
                member.PlanHistoryId == history.PlanHistoryId
                && member.MemberUserId == userId
                && member.Status == "accepted"
                && member.AcceptedAt <= now))
            && db.Plans.AsNoTracking().Any(plan => plan.PlanId == history.PlanId
                && (plan.Status == "active" || plan.Status == "archived")
                && EF.Functions.JsonContains(plan.Features, VideoPromotionFeatureJson)), ct);
    }

    private async Task<IReadOnlyList<VideoResponse>> ToResponsesAsync(IReadOnlyList<Video> videos, Guid? viewerId, CancellationToken ct)
    {
        if (videos.Count == 0) return [];
        var videoIds = videos.Select(video => video.VideoId).Distinct().ToArray();
        var channelIds = videos.Select(video => video.ChannelId).Distinct().ToArray();
        var channels = await db.Channels.AsNoTracking()
            .Where(channel => channelIds.Contains(channel.ChannelId))
            .ToDictionaryAsync(channel => channel.ChannelId, ct);
        var tagRows = await (from videoTag in db.VideoTags.AsNoTracking()
                             join tag in db.Tags.AsNoTracking() on videoTag.TagId equals tag.TagId
                             where videoIds.Contains(videoTag.VideoId)
                             select new { videoTag.VideoId, TagName = tag.Name }).ToListAsync(ct);
        var tagsByVideo = tagRows.GroupBy(row => row.VideoId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row.TagName).OrderBy(name => name).ToList());

        var reactionRows = await db.VideoReactions.AsNoTracking().Where(row => videoIds.Contains(row.VideoId))
            .GroupBy(row => new { row.VideoId, row.Type })
            .Select(group => new { group.Key.VideoId, group.Key.Type, Count = group.LongCount() }).ToListAsync(ct);
        var reactionStats = reactionRows.GroupBy(row => row.VideoId)
            .ToDictionary(group => group.Key, group => (
                Likes: group.Where(row => row.Type == "like").Select(row => row.Count).FirstOrDefault(),
                Dislikes: group.Where(row => row.Type == "dislike").Select(row => row.Count).FirstOrDefault()));
        var watchStats = await db.ViewingHistories.AsNoTracking().Where(row => videoIds.Contains(row.VideoId))
            .GroupBy(row => row.VideoId).Select(group => new { VideoId = group.Key, Seconds = group.Sum(row => (long)row.WatchDuration) })
            .ToDictionaryAsync(row => row.VideoId, row => row.Seconds, ct);
        var viewStats = await db.ViewingHistories.AsNoTracking().Where(row => videoIds.Contains(row.VideoId))
            .GroupBy(row => row.VideoId).Select(group => new { VideoId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(row => row.VideoId, row => row.Count, ct);
        var commentStats = await db.Comments.AsNoTracking().Where(row => videoIds.Contains(row.VideoId) && row.Status == "visible")
            .GroupBy(row => row.VideoId).Select(group => new { VideoId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.VideoId, row => row.Count, ct);
        var ratingRows = await db.VideoRatings.AsNoTracking().Where(row => videoIds.Contains(row.VideoId))
            .GroupBy(row => row.VideoId)
            .Select(group => new { VideoId = group.Key, Count = group.Count(), Average = group.Average(row => (double)row.Score) })
            .ToDictionaryAsync(row => row.VideoId, row => (row.Count, Average: (decimal?)row.Average), ct);
        var shareStats = await db.ShareHistories.AsNoTracking().Where(row => videoIds.Contains(row.VideoId))
            .GroupBy(row => row.VideoId).Select(group => new { VideoId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(row => row.VideoId, row => row.Count, ct);

        var viewerReactions = viewerId.HasValue
            ? await db.VideoReactions.AsNoTracking().Where(row => row.UserId == viewerId && videoIds.Contains(row.VideoId))
                .ToDictionaryAsync(row => row.VideoId, row => row.Type, ct)
            : new Dictionary<Guid, string>();
        var viewerRatings = viewerId.HasValue
            ? await db.VideoRatings.AsNoTracking().Where(row => row.UserId == viewerId && videoIds.Contains(row.VideoId))
                .ToDictionaryAsync(row => row.VideoId, row => (int?)row.Score, ct)
            : new Dictionary<Guid, int?>();
        Dictionary<Guid, ViewerHistoryRow> viewerHistory;
        if (viewerId.HasValue)
        {
            var historyRows = await db.ViewingHistories.AsNoTracking()
                .Where(row => row.UserId == viewerId && videoIds.Contains(row.VideoId))
                .OrderByDescending(row => row.ViewedAt).ThenByDescending(row => row.ViewingHistoryId)
                .Select(row => new { row.VideoId, row.WatchDuration, row.Progress })
                .ToListAsync(ct);
            viewerHistory = historyRows.GroupBy(row => row.VideoId)
                .ToDictionary(group => group.Key, group => new ViewerHistoryRow(group.First().WatchDuration, group.First().Progress));
        }
        else
        {
            viewerHistory = [];
        }

        var urlPaths = videos.SelectMany(video => new[] { video.ThumbnailUrl,
            channels.GetValueOrDefault(video.ChannelId)?.WatermarkUrl });
        var urls = await ResolveReadUrlsAsync(urlPaths, ct);
        return videos.Select(video =>
        {
            var channel = channels[video.ChannelId];
            reactionStats.TryGetValue(video.VideoId, out var reactions);
            ratingRows.TryGetValue(video.VideoId, out var rating);
            viewerHistory.TryGetValue(video.VideoId, out var history);
            viewerReactions.TryGetValue(video.VideoId, out var myReaction);
            viewerRatings.TryGetValue(video.VideoId, out var myRating);
            var stats = new VideoStatsResponse(viewStats.GetValueOrDefault(video.VideoId), reactions.Likes,
                reactions.Dislikes, commentStats.GetValueOrDefault(video.VideoId), rating.Average,
                rating.Count, shareStats.GetValueOrDefault(video.VideoId), watchStats.GetValueOrDefault(video.VideoId));
            var state = viewerId.HasValue
                ? new VideoViewerStateResponse(myReaction, myRating, history?.WatchDuration ?? 0, history?.Progress ?? 0)
                : null;
            return new VideoResponse(video.VideoId, video.ChannelId, channel.Name, channel.Handle, video.CategoryId,
                video.Title, video.Description, "", video.ThumbnailUrl == null ? null : urls.GetValueOrDefault(video.ThumbnailUrl),
                video.Duration, video.FileSize, video.Visibility, video.Status, video.ModerationStatus, video.LanguageCode,
                video.AgeRestricted, video.PublishedAt, video.CreatedAt, tagsByVideo.GetValueOrDefault(video.VideoId, []),
                ReadChapters(video.Metadata), stats, state, viewerId == channel.OwnerUserId ? video.ModerationReason : null,
                channel.WatermarkUrl == null ? null : urls.GetValueOrDefault(channel.WatermarkUrl), video.PromotionEnabled);
        }).ToList();
    }

    private async Task<Dictionary<string, string>> ResolveReadUrlsAsync(IEnumerable<string?> paths, CancellationToken ct)
    {
        var unique = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var resolved = await Task.WhenAll(unique.Select(async path =>
            (Path: path, Url: await ReadUrlAsync(path, ct))));
        return resolved.ToDictionary(item => item.Path, item => item.Url, StringComparer.Ordinal);
    }

    private async Task PublishInteractionNotificationAsync(Guid recipientId, Guid actorId, string type, string title,
        string content, string? actionUrl, string? resourceType, Guid? resourceId, CancellationToken ct)
    {
        if (recipientId == actorId) return;
        try
        {
            await notifications.PublishInAppAsync(recipientId, type, title, content, actionUrl, resourceType, resourceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reactions and comments are already persisted. A disconnected SignalR
            // client or a notification storage issue must not make the user retry
            // the interaction and accidentally create duplicates.
            logger.LogWarning(ex, "Could not publish {NotificationType} notification to {UserId}", type, recipientId);
        }
    }

    private static LibraryVideoResponse ToLibraryResponse(VideoResponse video, int watchedSeconds, decimal progress, DateTimeOffset activityAt) =>
        new(video.VideoId, video.ChannelId, video.ChannelName, video.ChannelHandle, video.Title, video.ThumbnailUrl,
            video.Duration, video.Visibility, video.PublishedAt, video.Stats.Views, video.Stats.Likes, video.Stats.Dislikes,
            video.Stats.AverageRating, video.Stats.RatingCount, video.ViewerState?.Rating, watchedSeconds, progress, activityAt);

    private async Task<CommentResponse> ToCommentAsync(Comment comment, Guid? viewerId, CancellationToken ct)
    {
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.UserId == comment.UserId, ct);
        var videoTitle = await db.Videos.IgnoreQueryFilters().AsNoTracking()
            .Where(video => video.VideoId == comment.VideoId).Select(video => video.Title).SingleOrDefaultAsync(ct);
        var likes = await db.CommentReactions.LongCountAsync(x => x.CommentId == comment.CommentId && x.Type == "like", ct);
        var dislikes = await db.CommentReactions.LongCountAsync(x => x.CommentId == comment.CommentId && x.Type == "dislike", ct);
        var mine = viewerId.HasValue ? await db.CommentReactions.AsNoTracking().SingleOrDefaultAsync(x => x.CommentId == comment.CommentId && x.UserId == viewerId, ct) : null;
        var replies = await db.Comments.CountAsync(x => x.ParentCommentId == comment.CommentId && x.Status == "visible", ct);
        return new(comment.CommentId, comment.VideoId, comment.UserId, user.DisplayName, comment.ParentCommentId, comment.Content, comment.Status, comment.CreatedAt, comment.UpdatedAt, likes, dislikes, mine?.Type, replies, videoTitle, comment.IsPinned, comment.HasCreatorHeart);
    }

    private async Task<IReadOnlyList<CommentResponse>> ToCommentsAsync(IReadOnlyList<Comment> comments, Guid? viewerId, CancellationToken ct)
    {
        if (comments.Count == 0) return [];
        var commentIds = comments.Select(comment => comment.CommentId).ToArray();
        var userIds = comments.Select(comment => comment.UserId).Distinct().ToArray();
        var videoIds = comments.Select(comment => comment.VideoId).Distinct().ToArray();
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(user => userIds.Contains(user.UserId))
            .ToDictionaryAsync(user => user.UserId, user => user.DisplayName, ct);
        var videoTitles = await db.Videos.IgnoreQueryFilters().AsNoTracking()
            .Where(video => videoIds.Contains(video.VideoId))
            .ToDictionaryAsync(video => video.VideoId, video => video.Title, ct);
        var reactionRows = await db.CommentReactions.AsNoTracking().Where(reaction => commentIds.Contains(reaction.CommentId))
            .GroupBy(reaction => new { reaction.CommentId, reaction.Type })
            .Select(group => new { group.Key.CommentId, group.Key.Type, Count = group.LongCount() }).ToListAsync(ct);
        var reactionStats = reactionRows.GroupBy(row => row.CommentId).ToDictionary(group => group.Key, group => (
            Likes: group.Where(row => row.Type == "like").Select(row => row.Count).FirstOrDefault(),
            Dislikes: group.Where(row => row.Type == "dislike").Select(row => row.Count).FirstOrDefault()));
        var replies = await db.Comments.AsNoTracking().Where(row => row.ParentCommentId.HasValue
                && commentIds.Contains(row.ParentCommentId.Value) && row.Status == "visible")
            .GroupBy(row => row.ParentCommentId!.Value)
            .Select(group => new { CommentId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.CommentId, row => row.Count, ct);
        var mine = viewerId.HasValue
            ? await db.CommentReactions.AsNoTracking().Where(reaction => reaction.UserId == viewerId && commentIds.Contains(reaction.CommentId))
                .ToDictionaryAsync(reaction => reaction.CommentId, reaction => reaction.Type, ct)
            : new Dictionary<Guid, string>();
        return comments.Select(comment =>
        {
            reactionStats.TryGetValue(comment.CommentId, out var stats);
            mine.TryGetValue(comment.CommentId, out var myReaction);
            return new CommentResponse(comment.CommentId, comment.VideoId, comment.UserId,
                users.GetValueOrDefault(comment.UserId, "Người dùng"), comment.ParentCommentId, comment.Content,
                comment.Status, comment.CreatedAt, comment.UpdatedAt, stats.Likes, stats.Dislikes, myReaction,
                replies.GetValueOrDefault(comment.CommentId), videoTitles.GetValueOrDefault(comment.VideoId), comment.IsPinned, comment.HasCreatorHeart);
        }).ToList();
    }

    private async Task<Video> RequireVideoAsync(Guid id, CancellationToken ct) => await db.Videos.SingleOrDefaultAsync(x => x.VideoId == id && x.Status != "deleted", ct) ?? throw Error(404, "VIDEO_NOT_FOUND", "Không tìm thấy video.");
    private async Task<Comment> RequireCommentAsync(Guid id, CancellationToken ct) => await db.Comments.SingleOrDefaultAsync(x => x.CommentId == id && x.Status != "deleted", ct) ?? throw Error(404, "COMMENT_NOT_FOUND", "Không tìm thấy bình luận.");

    private async Task EnsureCanViewAsync(Video video, Guid? viewerId, CancellationToken ct)
    {
        var channelActive = await db.Channels.AnyAsync(x => x.ChannelId == video.ChannelId && x.Status == "active", ct);
        if (video.MediaPurgedAt.HasValue || video.MediaPurgeStartedAt.HasValue || !channelActive || video.ModerationHidden)
            throw Error(viewerId.HasValue ? 403 : 404, "VIDEO_NOT_AVAILABLE", "Video không khả dụng.");
        // Local development can publish unlisted videos while moderation is disabled.
        // They must remain reachable by anyone holding the link, while public-feed
        // queries continue to require an approved public video.
        var canViewByLink = video.Status == "published" && video.Visibility == "unlisted"
            && (!features.ModerationEnabled || video.ModerationStatus == "approved");
        var canViewPublicly = video.Status == "published" && video.Visibility == "public"
            && video.ModerationStatus == "approved";
        if (canViewByLink || canViewPublicly) return;
        if (viewerId.HasValue && await HasChannelPermissionAsync(video.ChannelId, viewerId.Value, ChannelPermissions.VideoView, ct)) return;
        throw Error(viewerId.HasValue ? 403 : 404, "VIDEO_NOT_AVAILABLE", "Video không khả dụng.");
    }

    private IQueryable<Video> LibraryVisibleVideos(Guid userId) =>
        db.Videos.AsNoTracking().Where(video =>
            video.Status != "deleted" && video.MediaPurgedAt == null && video.MediaPurgeStartedAt == null
            && !video.ModerationHidden && db.Channels.Any(channel => channel.ChannelId == video.ChannelId && channel.Status == "active") &&
            ((video.Status == "published" &&
              ((video.Visibility == "public" && video.ModerationStatus == "approved") ||
               (video.Visibility == "unlisted" && (!features.ModerationEnabled || video.ModerationStatus == "approved"))))
             || db.Channels.Any(channel => channel.ChannelId == video.ChannelId && channel.OwnerUserId == userId)
             || db.ChannelMembers.Any(member => member.ChannelId == video.ChannelId && member.UserId == userId && member.Status == "active" &&
                 (member.RoleCode == ChannelRoles.Manager || member.RoleCode == ChannelRoles.Editor || member.RoleCode == ChannelRoles.Viewer))));

    private async Task<Channel> RequireChannelPermissionAsync(Guid channelId, Guid actorId, string permission, CancellationToken ct)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.ChannelId == channelId, ct) ?? throw Error(404, "CHANNEL_NOT_FOUND", "Không tìm thấy kênh.");
        if (channel.Status == "suspended") throw Error(403, "CHANNEL_SUSPENDED", "Kênh đã bị khóa do vi phạm chính sách cộng đồng.");
        if (channel.Status != "active") throw Error(404, "CHANNEL_NOT_FOUND", "Không tìm thấy kênh.");
        if (!await HasChannelPermissionAsync(channelId, actorId, permission, ct)) throw Error(403, "CHANNEL_PERMISSION_DENIED", "Bạn không có quyền thực hiện thao tác này trên kênh.");
        return channel;
    }

    private async Task<bool> HasChannelPermissionAsync(Guid channelId, Guid actorId, string permission, CancellationToken ct)
    {
        var channel = await db.Channels.AsNoTracking().SingleOrDefaultAsync(x => x.ChannelId == channelId, ct);
        if (channel?.OwnerUserId == actorId) return true;
        var role = await db.ChannelMembers.AsNoTracking().Where(x => x.ChannelId == channelId && x.UserId == actorId && x.Status == "active").Select(x => x.RoleCode).SingleOrDefaultAsync(ct);
        return ChannelPermissions.RoleHas(role, permission);
    }

    private async Task ReplaceTagsAsync(Guid videoId, IReadOnlyList<string> names, CancellationToken ct)
    {
        var old = await db.VideoTags.Where(x => x.VideoId == videoId).ToListAsync(ct);
        db.VideoTags.RemoveRange(old);
        if (names.Count == 0) return;
        var existing = await db.Tags.AsNoTracking().Where(tag => names.Contains(tag.Name)).ToListAsync(ct);
        var tagsByName = existing.ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
        var now = Now;
        foreach (var name in names)
        {
            if (!tagsByName.TryGetValue(name, out var tag))
            {
                tag = new Tag { Name = name, CreatedAt = now };
                db.Tags.Add(tag);
                tagsByName[name] = tag;
            }
            db.VideoTags.Add(new VideoTag { VideoId = videoId, TagId = tag.TagId, CreatedAt = now });
        }
    }

    private async Task<int> MaxDownloadHeightAsync(Guid userId, CancellationToken ct)
    {
        if (!features.PlanEnforcementEnabled) return int.MaxValue;
        var plan = await GetEffectivePlanForUserAsync(userId, ct);
        var quality = plan?.MaxDownloadQuality ?? plan?.MaxVideoQuality ?? "720p";
        return VideoRules.QualityHeight(quality) is > 0 and var value ? value : 720;
    }

    private async Task EnsureDownloadAllowedAsync(Guid userId, CancellationToken ct)
    {
        if (!features.PlanEnforcementEnabled) return;
        var plan = await GetEffectivePlanForUserAsync(userId, ct);
        if (plan == null || !PlanEntitlementRules.IsEnabled(plan.Features, PlanEntitlementRules.Download))
            throw Error(403, "DOWNLOAD_NOT_INCLUDED", "Gói hiện tại không bao gồm quyền tải video xuống.");
    }

    private async Task<Plan?> GetEffectivePlanForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
        var selected = user.PlanId.HasValue ? await GetEffectivePlanAsync(userId, user.PlanId.Value, ct) : null;
        var sharedPlan = await (from member in db.PlanMembers.AsNoTracking()
                                join history in db.PlanHistories.AsNoTracking() on member.PlanHistoryId equals history.PlanHistoryId
                                join plan in db.Plans.AsNoTracking() on history.PlanId equals plan.PlanId
                                where member.MemberUserId == userId && member.Status == "accepted"
                                      && history.Status == "active" && (!history.EndedAt.HasValue || history.EndedAt > Now)
                                      && (plan.Status == "active" || plan.Status == "archived")
                                orderby member.AcceptedAt descending
                                select plan).FirstOrDefaultAsync(ct);
        if (sharedPlan != null && (selected == null || selected.Price == 0 || user.PlanId == sharedPlan.PlanId)) return sharedPlan;
        if (selected != null) return selected;

        // Users without an active subscription still use the configured Free plan.
        // This keeps admin changes to the Free plan effective for legacy accounts
        // that predate plan histories.
        return await db.Plans.AsNoTracking()
            .Where(x => x.Code == "free" && x.Status == "active")
            .FirstOrDefaultAsync(ct);
    }

    private async Task<Guid> GetStorageBillingOwnerAsync(Guid channelOwnerUserId, CancellationToken ct)
    {
        var billingOwnerUserId = await (from member in db.PlanMembers.AsNoTracking()
                                        join history in db.PlanHistories.AsNoTracking() on member.PlanHistoryId equals history.PlanHistoryId
                                        where member.MemberUserId == channelOwnerUserId && member.Status == "accepted"
                                              && history.Status == "active" && (!history.EndedAt.HasValue || history.EndedAt > Now)
                                        orderby history.EndedAt descending
                                        select member.OwnerUserId).FirstOrDefaultAsync(ct);
        return billingOwnerUserId == Guid.Empty ? channelOwnerUserId : billingOwnerUserId;
    }

    private async Task<long> GetBilledStorageAsync(Guid billingOwnerUserId, CancellationToken ct) =>
        await db.Videos.AsNoTracking()
            .Where(video => video.StorageBillingOwnerUserId == billingOwnerUserId
                && video.Status != "deleted" && video.MediaPurgedAt == null)
            .SumAsync(video => (long?)video.FileSize, ct) ?? 0L;

    private async Task<long> GetChannelActiveVideoStorageAsync(Guid channelId, CancellationToken ct) =>
        await db.Videos.AsNoTracking()
            .Where(video => video.ChannelId == channelId && video.Status != "deleted" && video.MediaPurgedAt == null)
            .SumAsync(video => (long?)video.FileSize, ct) ?? 0L;

    private async Task LockStorageAsync(string key, CancellationToken ct)
    {
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    private async Task<Plan?> GetEffectivePlanAsync(Guid userId, Guid planId, CancellationToken ct)
    {
        // An archived offer still serves subscribers whose history has not expired.
        var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(x => x.PlanId == planId && (x.Status == "active" || x.Status == "archived"), ct);
        if (plan == null) return null;
        var now = Now;
        var hasSubscription = await db.PlanHistories.AsNoTracking().AnyAsync(x =>
            x.UserId == userId && x.PlanId == planId && x.Status == "active" && (!x.EndedAt.HasValue || x.EndedAt > now), ct)
            || await (from member in db.PlanMembers.AsNoTracking()
                      join history in db.PlanHistories.AsNoTracking() on member.PlanHistoryId equals history.PlanHistoryId
                      where member.MemberUserId == userId && member.Status == "accepted" && history.PlanId == planId
                            && history.Status == "active" && (!history.EndedAt.HasValue || history.EndedAt > now)
                      select member.PlanMemberId).AnyAsync(ct);
        return hasSubscription ? plan : null;
    }

    private static IReadOnlyList<VideoChapter> ReadChapters(string metadata)
    {
        try { return JsonSerializer.Deserialize<Metadata>(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Chapters ?? []; }
        catch (JsonException) { return []; }
    }
    private static IReadOnlyList<VideoCardRequest> ReadVideoCardReferences(string metadata)
    {
        try { return JsonSerializer.Deserialize<Metadata>(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.VideoCards ?? []; }
        catch (JsonException) { return []; }
    }
    private sealed record Metadata(IReadOnlyList<VideoChapter>? Chapters, IReadOnlyList<VideoCardRequest>? VideoCards = null);
    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags) => (tags ?? []).Select(x => x.Trim().TrimStart('#').ToLowerInvariant()).Where(x => x.Length is > 0 and <= 80).Distinct().Take(20).ToArray();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void EnsureVisibilityAvailable(string visibility)
    {
        if (!features.ModerationEnabled && visibility.Equals("public", StringComparison.OrdinalIgnoreCase))
            throw Error(409, "PUBLICATION_LOCKED", "Chế độ công khai đang tạm khóa cho đến khi kiểm duyệt được bật.");
    }
    private static ModerationCase CreateUploadModerationCase(Video video, DateTimeOffset now) => new()
    {
        VideoId = video.VideoId,
        Status = "pending",
        CaseType = "upload_review",
        RiskLevel = ModerationRiskLevel(video.Title, video.Description),
        SubmittedAt = now,
        UpdatedAt = now
    };
    private static string ModerationRiskLevel(string title, string? description)
    {
        var combinedText = $"{title} {description}".ToLowerInvariant();
        var highRiskKeywords = new[] { "sex", "khiêu dâm", "đồi trụy", "18+", "giết người", "tự tử", "chém", "bom", "hack", "lừa đảo" };
        return highRiskKeywords.Any(k => combinedText.Contains(k)) ? "high" : "low";
    }
    private static (int Page, int PageSize) Page(int page, int size) => (Math.Max(1, page), Math.Clamp(size, 1, 50));
    private static ContentException Error(int status, string code, string message) => new(status, code, message);
    private async Task RecordUploadFailureAsync(Guid actorId, Guid videoId, string title, Exception exception)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = actorId,
                Action = "video.upload.failed",
                ResourceType = "video_upload",
                ResourceId = videoId,
                Reason = $"{title}: {exception.GetType().Name}",
                CreatedAt = Now
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception auditException)
        {
            logger.LogError(auditException, "Could not persist failed video upload event {VideoId}", videoId);
        }
    }
    private Task<string> ReadUrlAsync(string path, CancellationToken ct) => storage.GetReadUrlAsync(path, TimeSpan.FromMinutes(60), ct);
}
