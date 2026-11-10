using HuTube.Infrastructure.Videos;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HuTube.Application.Auth;
using HuTube.Application.Channels;
using HuTube.Application.Videos;
using HuTube.Domain.Rbac;
using HuTube.Domain.Users;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Recommendations;

public sealed record MatrixCounts(int Pairs, int Users, int Videos);
public sealed record MatrixPreviewResponse(string[] Columns, IReadOnlyList<string[]> Rows, int Total,
    string[]? DisplayColumns = null, IReadOnlyList<string[]>? DisplayRows = null);
public sealed record MatrixDiffResponse(string State, string? ModelVersion, string? CsvKey,
    string? CsvSha256, DateTimeOffset? UpdatedAt, MatrixCounts Active, MatrixCounts Current,
    int Added, int Changed, int Removed, double ChangeRate, string? ModelAlgorithm = null, ScoreAggregationConfig? ScoreAggregation = null);
public sealed record AdminUserOption(Guid UserId, string Username, string DisplayName, bool IsBot);
public sealed record AdminCategoryOption(Guid CategoryId, string Name, string Slug);
public sealed record AdminVideoOption(Guid VideoId, Guid ChannelId, string Title, int Duration,
    Guid? CategoryId, string CategoryName, string CategorySlug);
public sealed record AdminVideoPage(IReadOnlyList<AdminVideoOption> Items, int Page, int PageSize, int Total);
public sealed record CreateBotsRequest(int Count, string Prefix, string? DisplayName = null);
public sealed record SimulationCategoryRate(Guid CategoryId, int Rate);
public sealed record SimulationCategoryComments(Guid CategoryId, IReadOnlyList<string> Templates);
// Nullable for compatibility with queued payloads from the previous fixed-percent UI;
// new simulations ignore this legacy value and sample 1–100% per watched video.
public sealed record SimulatorRequest(IReadOnlyList<Guid> UserIds, IReadOnlyList<Guid> VideoIds,
    string Mode, int ActionsPerUser, int DelayMs, int? WatchPercent,
    int ViewRate, int LikeRate, int DislikeRate, int RatingRate, int CommentRate,
    int SubscribeRate, IReadOnlyList<string> CommentTemplates, bool ConfirmRealUsers,
    Guid? PreferredCategoryId = null, int PreferredCategoryRatio = 80,
    IReadOnlyList<SimulationCategoryRate>? CategoryRates = null,
    int VideoSkipRate = 0, IReadOnlyList<SimulationCategoryComments>? CategoryComments = null);
public sealed record ModelUpdateRequest(string? Mode = null, Dictionary<string, decimal>? Weights = null,
    string? ModelAlgorithm = null);
public sealed record ScoreAggregationConfig(string Mode, Dictionary<string, decimal> Weights);
public sealed record ModelManifest(string ModelVersion, string CsvKey, string CsvSha256,
    string ArtifactKey, string ArtifactSha256, DateTimeOffset UpdatedAt,
    ScoreAggregationConfig? ScoreAggregation = null, string ModelAlgorithm = "item_based");
public sealed record ModelStatisticsItem(string ModelVersion, string ModelAlgorithm,
    DateTimeOffset? BuildStartedAt, DateTimeOffset? BuildCompletedAt,
    double? BuildDurationSeconds, double? FitDurationSeconds,
    int? UsersCount, int? VideosCount, int? InteractionsCount,
    DateTimeOffset? LastUsedAt, bool IsActive);
public sealed record ModelStatisticsResponse(string? ActiveModelVersion,
    IReadOnlyList<ModelStatisticsItem> Items);

public sealed class RecommendationAdminService(
    HuTubeDbContext db, IRecommendationSnapshotStore snapshots,
    IHttpClientFactory clients, RecommendationOptions options,
    IPasswordService passwords, IContentService content, ChannelService channels,
    ILogger<RecommendationAdminService> logger,
    BackgroundWorkWakeup<RecommendationJobWorker>? jobWakeup = null)
{
    private const int VideoPickerPageSize = 200;
    private const int MaxSimulationVideoCount = 10_000;
    private const int MaxSimulationActionCount = 50_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ScoreFeatures = ["rating", "like", "dislike", "watch", "comment", "subscribe"];
    private static readonly string[] ModelAlgorithms = [
        "item_based", "incremental", "batch_incremental", "batch_incremental_partial_topk"];
    private static ContentException Error(int status, string code, string message) => new(status, code, message);
    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static ScoreAggregationConfig DefaultScoreAggregation() =>
        new("average", ScoreFeatures.ToDictionary(feature => feature, _ => 1m,
            StringComparer.OrdinalIgnoreCase));

    private static string NormalizeModelAlgorithm(ModelUpdateRequest? request)
    {
        var value = string.IsNullOrWhiteSpace(request?.ModelAlgorithm)
            ? "item_based"
            : request.ModelAlgorithm.Trim().ToLowerInvariant();
        if (!ModelAlgorithms.Contains(value, StringComparer.Ordinal))
            throw Error(400, "INVALID_MODEL_ALGORITHM", "Thuật toán model không hợp lệ.");
        return value;
    }

    private static ScoreAggregationConfig NormalizeScoreAggregation(ModelUpdateRequest? request)
    {
        var mode = string.IsNullOrWhiteSpace(request?.Mode)
            ? "average"
            : request.Mode.Trim().ToLowerInvariant();
        if (mode is not ("average" or "weighted"))
            throw Error(400, "INVALID_SCORE_MODE", "Phương thức score phải là average hoặc weighted.");
        if (mode == "average") return DefaultScoreAggregation();

        var input = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request?.Weights ?? []) input[pair.Key] = pair.Value;
        var unknown = input.Keys.Where(key => !ScoreFeatures.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0)
            throw Error(400, "INVALID_SCORE_FEATURE", $"Thuộc tính score không hợp lệ: {string.Join(", ", unknown)}.");
        var weights = ScoreFeatures.ToDictionary(
            feature => feature,
            feature => input.TryGetValue(feature, out var value) ? value : 0m,
            StringComparer.OrdinalIgnoreCase);
        if (weights.Values.Any(value => value < 0m) || weights.Values.All(value => value == 0m))
            throw Error(400, "INVALID_SCORE_WEIGHTS", "Trọng số phải không âm và cần có ít nhất một trọng số lớn hơn 0.");
        var maximum = weights.Values.Max();
        return new(mode, weights.ToDictionary(x => x.Key, x => x.Value / maximum, StringComparer.OrdinalIgnoreCase));
    }

    private static bool MatchesPublishedModel(ModelManifest? manifest, string key, string hash,
        ScoreAggregationConfig requested, string requestedAlgorithm = "item_based") =>
        manifest?.CsvKey == key && string.Equals(manifest.CsvSha256, hash, StringComparison.OrdinalIgnoreCase)
        && string.Equals(manifest.ModelAlgorithm ?? "item_based", requestedAlgorithm, StringComparison.Ordinal)
        && (manifest.ScoreAggregation ?? DefaultScoreAggregation()).Mode == requested.Mode
        && ScoreFeatures.All(feature => Math.Abs((manifest.ScoreAggregation ?? DefaultScoreAggregation()).Weights.GetValueOrDefault(feature) - requested.Weights.GetValueOrDefault(feature)) <= 0.000000000001m);

    private static ScoreAggregationConfig ScoreAggregationFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson == "{}") return DefaultScoreAggregation();
        var request = JsonSerializer.Deserialize<ModelUpdateRequest>(payloadJson, JsonOptions)
            ?? new ModelUpdateRequest();
        return NormalizeScoreAggregation(request);
    }

    private static string ModelAlgorithmFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson == "{}") return "item_based";
        var request = JsonSerializer.Deserialize<ModelUpdateRequest>(payloadJson, JsonOptions)
            ?? new ModelUpdateRequest();
        return NormalizeModelAlgorithm(request);
    }

    private sealed class Signal(Guid userId, Guid videoId)
    {
        public Guid UserId { get; } = userId;
        public Guid VideoId { get; } = videoId;
        public decimal WatchRatio { get; set; }
        public bool Watched { get; set; }
        public bool Like { get; set; }
        public bool Dislike { get; set; }
        public short? Rating { get; set; }
        public int Comments { get; set; }
        public bool Subscribed { get; set; }
        public decimal Score(ScoreAggregationConfig config)
        {
            var signals = new (string Feature, decimal Value, bool Observed)[]
            {
                ("rating", Rating.HasValue ? (Rating.Value - 1m) / 4m : 0m, Rating.HasValue),
                ("like", Like ? 1m : 0m, Like),
                // A dislike is an observed negative preference, so its normalized
                // preference value is zero instead of adding positive evidence.
                ("dislike", 0m, Dislike),
                ("watch", WatchRatio, Watched),
                ("comment", Comments > 0 ? 1m : 0m, Comments > 0),
                ("subscribe", Subscribed ? 1m : 0m, Subscribed)
            };
            decimal total = 0m;
            decimal totalWeight = 0m;
            foreach (var signal in signals)
            {
                if (!signal.Observed || !config.Weights.TryGetValue(signal.Feature, out var weight) || weight <= 0m)
                    continue;
                total += signal.Value * weight;
                totalWeight += weight;
            }
            return totalWeight == 0m ? 0m : Math.Clamp(total / totalWeight, 0m, 1m);
        }

        public string CsvLine(ScoreAggregationConfig config) => string.Join(",", new[] { UserId.ToString(), VideoId.ToString(),
            WatchRatio.ToString("0.####", CultureInfo.InvariantCulture),
            Like ? "1" : "0", Dislike ? "1" : "0", Rating?.ToString(CultureInfo.InvariantCulture) ?? "",
            Comments.ToString(CultureInfo.InvariantCulture), Subscribed ? "1" : "0",
            Score(config).ToString("0.####", CultureInfo.InvariantCulture) });
    }

    private async Task<List<Signal>> BuildMatrixAsync(CancellationToken ct)
    {
        var activeUsers = db.Users.AsNoTracking().Where(x => x.Status == "active");
        var publicVideos = VideoAccessPolicy.Catalogue(db)
            .Where(x => x.Status == "published" && x.ModerationStatus == "approved" && x.Visibility == "public");
        var userIds = (await activeUsers.Select(x => x.UserId).ToListAsync(ct)).ToHashSet();
        var videos = await publicVideos.Select(x => new { x.VideoId, x.ChannelId }).ToListAsync(ct);
        var videoIds = videos.Select(x => x.VideoId).ToHashSet();
        var pairs = new Dictionary<(Guid, Guid), Signal>();
        Signal? Get(Guid userId, Guid videoId)
        {
            if (!userIds.Contains(userId) || !videoIds.Contains(videoId)) return null;
            var key = (userId, videoId);
            if (!pairs.TryGetValue(key, out var signal))
                pairs[key] = signal = new Signal(userId, videoId);
            return signal;
        }
        var histories = from history in db.ViewingHistories.AsNoTracking()
                        join user in activeUsers on history.UserId equals user.UserId
                        join video in publicVideos on history.VideoId equals video.VideoId
                        where history.WatchDuration > 0
                        select new { history.UserId, history.VideoId, history.Progress };
        await foreach (var h in histories.AsAsyncEnumerable().WithCancellation(ct))
        {
            var row = Get(h.UserId, h.VideoId);
            if (row != null)
            {
                row.Watched = true;
                row.WatchRatio = Math.Max(row.WatchRatio, Math.Clamp(h.Progress / 100m, 0, 1));
            }
        }
        var reactions = from reaction in db.VideoReactions.AsNoTracking()
                        join user in activeUsers on reaction.UserId equals user.UserId
                        join video in publicVideos on reaction.VideoId equals video.VideoId
                        select new { reaction.UserId, reaction.VideoId, reaction.Type };
        await foreach (var r in reactions.AsAsyncEnumerable().WithCancellation(ct))
        {
            var row = Get(r.UserId, r.VideoId);
            if (row == null) continue;
            row.Like = r.Type == "like";
            row.Dislike = r.Type == "dislike";
        }
        var ratings = from rating in db.VideoRatings.AsNoTracking()
                      join user in activeUsers on rating.UserId equals user.UserId
                      join video in publicVideos on rating.VideoId equals video.VideoId
                      select new { rating.UserId, rating.VideoId, rating.Score };
        await foreach (var r in ratings.AsAsyncEnumerable().WithCancellation(ct))
        {
            var row = Get(r.UserId, r.VideoId);
            if (row != null) row.Rating = r.Score;
        }
        var comments = from comment in db.Comments.AsNoTracking()
                       join user in activeUsers on comment.UserId equals user.UserId
                       join video in publicVideos on comment.VideoId equals video.VideoId
                       where comment.Status == "visible"
                       group comment by new { comment.UserId, comment.VideoId } into grouped
                       select new { grouped.Key.UserId, grouped.Key.VideoId, Count = grouped.Count() };
        await foreach (var c in comments.AsAsyncEnumerable().WithCancellation(ct))
        {
            var row = Get(c.UserId, c.VideoId);
            if (row != null) row.Comments = c.Count;
        }
        var publicChannelIds = publicVideos.Select(video => video.ChannelId).Distinct();
        var subscriptions = await (from subscription in db.Subscriptions.AsNoTracking()
                                   join user in activeUsers on subscription.UserId equals user.UserId
                                   join channelId in publicChannelIds on subscription.ChannelId equals channelId
                                   where subscription.Status == "active"
                                   select new { subscription.UserId, subscription.ChannelId })
            .Distinct().ToListAsync(ct);
        var channelByVideo = videos.ToDictionary(x => x.VideoId, x => x.ChannelId);
        var subscribedChannels = subscriptions.Select(x => (x.UserId, x.ChannelId)).ToHashSet();
        // Channel subscriptions enrich video interactions already present. Creating a
        // pair for every video in a subscribed channel would mark unseen videos as seen.
        foreach (var row in pairs.Values)
            row.Subscribed = subscribedChannels.Contains((row.UserId, channelByVideo[row.VideoId]));
        return pairs.Values.OrderBy(x => x.UserId).ThenBy(x => x.VideoId).ToList();
    }

    private static byte[] ToCsv(IReadOnlyList<Signal> rows, ScoreAggregationConfig config)
    {
        var builder = new StringBuilder("user_id,video_id,watch_ratio,like,dislike,rating,comment_count,subscribed,score\n");
        foreach (var row in rows) builder.Append(row.CsvLine(config)).Append('\n');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private async Task<ModelManifest?> ActiveAsync(CancellationToken ct)
    {
        var bytes = await snapshots.ReadOptionalAsync("collaborative_cf/active.json", ct);
        return bytes == null ? null : JsonSerializer.Deserialize<ModelManifest>(bytes, JsonOptions);
    }

    public async Task<ModelStatisticsResponse> ModelStatisticsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceUrl) || string.IsNullOrWhiteSpace(options.AdminToken))
            throw Error(503, "MODEL_STATISTICS_UNAVAILABLE", "Chưa cấu hình kết nối thống kê mô hình.");
        using var client = clients.CreateClient("recommendation-model-statistics");
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RecommendationWakeupPolicy.GetTimeout(options.TimeoutSeconds));
        var retryAttempt = 0;
        try
        {
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    options.ServiceUrl.TrimEnd('/') + "/internal/models");
                request.Headers.Add("X-Model-Admin-Token", options.AdminToken);

                using var response = await client.SendAsync(request, timeoutCts.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw Error(503, "MODEL_ADMIN_TOKEN_MISMATCH", "Token quản trị của API và Recommendation Service chưa khớp.");

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    if (RecommendationWakeupPolicy.IsTransientStartupResponse(response.StatusCode, errorBody))
                    {
                        retryAttempt++;
                        var delay = RecommendationWakeupPolicy.GetRetryDelay(retryAttempt);
                        logger.LogInformation(
                            "Recommendation Service returned HTTP {StatusCode} while starting. Retrying model statistics in {Delay} (attempt {Attempt}).",
                            response.StatusCode, delay, retryAttempt);
                        await Task.Delay(delay, timeoutCts.Token);
                        continue;
                    }

                    logger.LogWarning("Recommendation Service returned HTTP {StatusCode} for model statistics: {Error}",
                        response.StatusCode, errorBody);
                    throw Error(503, "MODEL_STATISTICS_UNAVAILABLE", "Không đọc được thống kê mô hình từ Recommendation Service. Vui lòng thử lại.");
                }

                return await response.Content.ReadFromJsonAsync<ModelStatisticsResponse>(JsonOptions, timeoutCts.Token)
                    ?? throw Error(503, "MODEL_STATISTICS_UNAVAILABLE", "Recommendation Service không trả về thống kê mô hình.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read recommendation model statistics.");
            throw Error(503, "MODEL_STATISTICS_UNAVAILABLE", "Dịch vụ thống kê mô hình chưa sẵn sàng. Vui lòng thử lại.");
        }
    }

    public async Task<MatrixDiffResponse> DiffAsync(CancellationToken ct)
    {
        var active = await ActiveAsync(ct);
        var scoreAggregation = active?.ScoreAggregation ?? DefaultScoreAggregation();
        var rows = await BuildMatrixAsync(ct);
        var current = Count(rows.Select(x => x.CsvLine(scoreAggregation)));
        if (active == null)
            return new("uninitialized", null, null, null, null,
                new MatrixCounts(0, 0, 0), current, rows.Count, 0, 0, 0);
        var oldBytes = await snapshots.ReadOptionalAsync(active.CsvKey, ct)
            ?? throw Error(503, "ACTIVE_CSV_MISSING", "CSV của model đang hoạt động không còn trên R2.");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(oldBytes)), active.CsvSha256, StringComparison.OrdinalIgnoreCase))
            throw Error(503, "ACTIVE_CSV_INVALID", "Hash CSV của model đang hoạt động không khớp.");
        var oldLines = Encoding.UTF8.GetString(oldBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(x => x.TrimEnd('\r')).ToList();
        static string Key(string line) { var cols = line.Split(',', 3); return cols[0] + "," + cols[1]; }
        var oldMap = oldLines.ToDictionary(Key, x => x, StringComparer.Ordinal);
        var newMap = rows.ToDictionary(x => $"{x.UserId},{x.VideoId}", x => x.CsvLine(scoreAggregation), StringComparer.Ordinal);
        var added = newMap.Keys.Count(x => !oldMap.ContainsKey(x));
        var removed = oldMap.Keys.Count(x => !newMap.ContainsKey(x));
        var changed = newMap.Count(x => oldMap.TryGetValue(x.Key, out var old) && old != x.Value);
        return new("ready", active.ModelVersion, active.CsvKey, active.CsvSha256, active.UpdatedAt,
            Count(oldLines), current, added, changed, removed,
            oldMap.Count == 0 ? 0 : (double)(added + changed + removed) / oldMap.Count,
            active.ModelAlgorithm, active.ScoreAggregation ?? DefaultScoreAggregation());
    }

    private static MatrixCounts Count(IEnumerable<string> lines)
    {
        var values = lines.Select(x => x.Split(',', 3)).ToList();
        return new(values.Count, values.Select(x => x[0]).Distinct().Count(),
            values.Select(x => x[1]).Distinct().Count());
    }

    public async Task<byte[]> ExportMatrixAsync(CancellationToken ct) =>
        ToCsv(await BuildMatrixAsync(ct), (await ActiveAsync(ct))?.ScoreAggregation ?? DefaultScoreAggregation());

    public async Task<MatrixPreviewResponse> PreviewMatrixAsync(CancellationToken ct)
    {
        var scoreConfig = (await ActiveAsync(ct))?.ScoreAggregation ?? DefaultScoreAggregation();
        var rows = await BuildMatrixAsync(ct);
        var previewRows = rows.Take(20).ToList();
        var userIds = previewRows.Select(x => x.UserId).Distinct().ToArray();
        var videoIds = previewRows.Select(x => x.VideoId).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, x => new { x.DisplayName, x.Username }, ct);
        var videos = await VideoAccessPolicy.Catalogue(db).Where(x => videoIds.Contains(x.VideoId))
            .Select(x => new { x.VideoId, x.Title, x.CategoryId }).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking()
            .ToDictionaryAsync(x => x.CategoryId, x => x.Name, ct);
        var displayRows = previewRows.Select(row =>
        {
            users.TryGetValue(row.UserId, out var user);
            var video = videos.FirstOrDefault(x => x.VideoId == row.VideoId);
            var categoryName = video?.CategoryId is Guid categoryId && categories.TryGetValue(categoryId, out var name)
                ? name : "";
            return new[]
            {
                user?.DisplayName ?? "",
                user?.Username ?? "",
                video?.Title ?? "",
                categoryName,
                (row.WatchRatio * 100m).ToString("0.#", CultureInfo.InvariantCulture),
                row.Like ? "like" : row.Dislike ? "dislike" : "",
                row.Rating?.ToString(CultureInfo.InvariantCulture) ?? "",
                row.Subscribed ? "yes" : "",
                row.Comments.ToString(CultureInfo.InvariantCulture)
            };
        }).ToList();
        return new MatrixPreviewResponse(
            ["user_id", "video_id", "watch_ratio", "like", "dislike", "rating",
                "comment_count", "subscribed", "score"],
            previewRows.Select(x => x.CsvLine(scoreConfig).Split(',')).ToList(), rows.Count,
            ["viewer_name", "viewer_username", "video", "category", "watch_percent",
                "reaction", "rating", "subscription", "comments"], displayRows);
    }

    public async Task<RecommendationJob> QueueModelAsync(Guid actorId, ModelUpdateRequest? request, CancellationToken ct)
    {
        if (await db.RecommendationJobs.AnyAsync(x => x.Kind == "model_update" && (x.Status == "queued" || x.Status == "running"), ct))
            throw Error(409, "MODEL_UPDATE_RUNNING", "Một lượt cập nhật model đang chạy.");
        var scoreAggregation = NormalizeScoreAggregation(request);
        var modelAlgorithm = NormalizeModelAlgorithm(request);
        var job = new RecommendationJob {
            ActorUserId = actorId,
            Kind = "model_update",
            PayloadJson = Serialize(new {
                modelAlgorithm,
                mode = scoreAggregation.Mode,
                weights = scoreAggregation.Weights,
            })
        };
        db.RecommendationJobs.Add(job);
        await db.SaveChangesAsync(ct);
        jobWakeup?.Signal();
        return job;
    }

    public async Task<IReadOnlyList<AdminUserOption>> UsersAsync(string? search, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking().Where(x => x.Status == "active");
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Username, $"%{term}%")
                || EF.Functions.ILike(x.DisplayName, $"%{term}%"));
        }
        var users = await query.OrderBy(x => x.Username)
            .Take(200)
            .Select(x => new { x.UserId, x.Username, x.DisplayName, x.Email }).ToListAsync(ct);
        return users.Select(x => new AdminUserOption(x.UserId, x.Username, x.DisplayName,
            x.Email.EndsWith(".hutube.invalid", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public async Task<IReadOnlyList<AdminCategoryOption>> CategoriesAsync(CancellationToken ct) =>
        await db.Categories.AsNoTracking().Where(x => x.Status == "active")
            .OrderBy(x => x.Name)
            .Select(x => new AdminCategoryOption(x.CategoryId, x.Name, x.Slug))
            .ToListAsync(ct);

    public async Task<AdminVideoPage> VideosAsync(string? search, Guid? categoryId,
        string? categoryIds, int page, CancellationToken ct)
    {
        var query = VideoAccessPolicy.Catalogue(db).Where(x => x.Status == "published" && x.ModerationStatus == "approved" && x.Visibility == "public");
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => EF.Functions.ILike(x.Title, $"%{search.Trim()}%"));
        if (categoryId.HasValue) query = query.Where(x => x.CategoryId == categoryId.Value);
        var categoryIdList = (categoryIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
            .Where(value => value.HasValue).Select(value => value!.Value).Distinct().ToArray();
        if (categoryIdList.Length > 0) query = query.Where(x => x.CategoryId.HasValue && categoryIdList.Contains(x.CategoryId.Value));
        var total = await query.CountAsync(ct);
        var normalizedPage = Math.Max(1, page);
        var offset = (int)Math.Min(int.MaxValue, (long)(normalizedPage - 1) * VideoPickerPageSize);
        var videos = await query.OrderByDescending(x => x.PublishedAt).ThenBy(x => x.VideoId)
            .Skip(offset).Take(VideoPickerPageSize)
            .Select(x => new { x.VideoId, x.ChannelId, x.Title, x.Duration, x.CategoryId }).ToListAsync(ct);
        var videoCategoryIds = videos.Where(x => x.CategoryId.HasValue).Select(x => x.CategoryId!.Value).Distinct().ToArray();
        var categories = await db.Categories.AsNoTracking().Where(x => videoCategoryIds.Contains(x.CategoryId))
            .ToDictionaryAsync(x => x.CategoryId, x => new { x.Name, x.Slug }, ct);
        var items = videos.Select(x =>
        {
            var category = x.CategoryId is Guid id && categories.TryGetValue(id, out var found) ? found : null;
            return new AdminVideoOption(x.VideoId, x.ChannelId, x.Title, x.Duration,
                x.CategoryId, category?.Name ?? "", category?.Slug ?? "default");
        }).ToList();
        return new AdminVideoPage(items, normalizedPage, VideoPickerPageSize, total);
    }

    private sealed record SimulationVideo(Guid VideoId, Guid ChannelId, Guid UploadedByUserId,
        int Duration, Guid? CategoryId);

    public async Task<IReadOnlyList<AdminUserOption>> CreateBotsAsync(Guid actorId, CreateBotsRequest request, CancellationToken ct)
    {
        if (request.Count is < 1 or > 100 || string.IsNullOrWhiteSpace(request.Prefix)
            || !System.Text.RegularExpressions.Regex.IsMatch(request.Prefix, "^[A-Za-z][A-Za-z0-9_-]{1,20}$"))
            throw Error(400, "INVALID_BOT_REQUEST", "Tạo 1–100 bot với tiền tố gồm 2–21 ký tự chữ, số, _ hoặc -.");
        if (request.DisplayName is { } displayName && displayName.Trim().Length is < 2 or > 80)
            throw Error(400, "INVALID_BOT_DISPLAY_NAME", "Tên hiển thị của bot cần từ 2 đến 80 ký tự.");
        var now = DateTimeOffset.UtcNow;
        var bots = new List<User>();
        for (var i = 0; i < request.Count; i++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var username = $"{request.Prefix}_{suffix}";
            bots.Add(new User { UserId = Guid.NewGuid(), Username = username,
                DisplayName = request.Count == 1 && !string.IsNullOrWhiteSpace(request.DisplayName)
                    ? request.DisplayName.Trim() : $"Bot {i + 1}", Email = $"{username}@bot.hutube.invalid",
                PasswordHash = passwords.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))),
                RoleId = UserRoles.User, Status = "active", EmailVerifiedAt = now,
                CreatedAt = now, UpdatedAt = now });
        }
        db.Users.AddRange(bots);
        db.AuditLogs.Add(new AuditLog { ActorUserId = actorId, Action = "recommendations.bots_created",
            ResourceType = "recommendation_bot", NewValues = Serialize(new { users = bots.Select(x => x.UserId) }), CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return bots.Select(x => new AdminUserOption(x.UserId, x.Username, x.DisplayName, true)).ToList();
    }

    public async Task<RecommendationJob> QueueSimulatorAsync(Guid actorId, SimulatorRequest request, CancellationToken ct)
    {
        if (request.UserIds is not { Count: >= 1 and <= 200 })
            throw Error(400, "INVALID_SIMULATION_USERS", "Mô phỏng cần từ 1 đến 200 user.");
        if (request.VideoIds is not { Count: >= 1 and <= MaxSimulationVideoCount })
            throw Error(400, "INVALID_SIMULATION_VIDEOS", $"Mô phỏng cần từ 1 đến {MaxSimulationVideoCount:N0} video.");
        if (request.ActionsPerUser is < 1 or > 1000)
            throw Error(400, "INVALID_SIMULATION_ACTIONS", "Số video tương tác mỗi user cần từ 1 đến 1.000.");
        if ((long)request.UserIds.Count * request.ActionsPerUser > MaxSimulationActionCount)
            throw Error(400, "SIMULATION_ACTION_LIMIT", "Một lượt mô phỏng không thể vượt quá 50.000 hành động; hãy giảm số user hoặc số video mỗi user.");
        if (request.DelayMs is < 0 or > 5000)
            throw Error(400, "INVALID_SIMULATION_DELAY", "Độ trễ cần nằm trong khoảng 0 đến 5.000 ms.");
        if (request.Mode is not ("target" or "cluster"))
            throw Error(400, "INVALID_SIMULATION_MODE", "Kịch bản mô phỏng không hợp lệ.");
        if (request.CommentTemplates is null
            || request.CommentTemplates.Any(x => x is null || x.Length is < 3 or > 5000))
            throw Error(400, "INVALID_SIMULATION_COMMENTS", "Mỗi mẫu bình luận cần từ 3 đến 5.000 ký tự.");
        var categoryComments = request.CategoryComments ?? [];
        if (categoryComments.Count > 20 || categoryComments.Any(x => x is null || x.CategoryId == Guid.Empty
                || x.Templates is null
                || x.Templates.Any(text => text is null || text.Trim().Length is < 3 or > 5000))
            || categoryComments.GroupBy(x => x.CategoryId).Any(group => group.Count() > 1))
            throw Error(400, "INVALID_SIMULATION_COMMENTS", "Mỗi mẫu bình luận cần từ 3 đến 5.000 ký tự; tối đa 20 danh mục khác nhau.");
        if (categoryComments.Count > 0)
        {
            var commentCategoryIds = categoryComments.Select(x => x.CategoryId).ToArray();
            if (await db.Categories.CountAsync(x => commentCategoryIds.Contains(x.CategoryId) && x.Status == "active", ct) != categoryComments.Count)
                throw Error(400, "CATEGORY_NOT_ACTIVE", "Danh mục bình luận không hoạt động hoặc không tồn tại.");
        }
        if (new[] { request.ViewRate, request.LikeRate, request.DislikeRate, request.RatingRate,
                request.CommentRate, request.SubscribeRate, request.VideoSkipRate }.Any(x => x is < 0 or > 100))
            throw Error(400, "INVALID_SIMULATION_RATE", "Tỷ lệ hành vi phải từ 0 đến 100.");
        if (request.LikeRate + request.DislikeRate > 100)
            throw Error(400, "INVALID_REACTION_RATE", "Tổng tỷ lệ thích và không thích không được vượt 100%.");
        if (request.Mode == "cluster" && request.PreferredCategoryRatio is < 50 or > 100)
            throw Error(400, "INVALID_CATEGORY_RATIO", "Tỷ lệ ưu tiên danh mục cần từ 50 đến 100%.");
        var categoryRates = request.CategoryRates ?? [];
        var categoryRateIds = categoryRates.Select(rate => rate.CategoryId).Distinct().ToArray();
        if (categoryRates.Count > 20 || categoryRates.GroupBy(x => x.CategoryId).Any(group => group.Count() > 1)
            || categoryRates.Any(x => x.Rate is < 1 or > 100)
            || (categoryRates.Count > 0 && categoryRates.Sum(x => x.Rate) != 100))
            throw Error(400, "INVALID_CATEGORY_RATES", "Tỷ lệ danh mục phải là các danh mục khác nhau, mỗi tỷ lệ từ 1 đến 100% và tổng bằng 100%.");
        if (categoryRates.Count > 0)
        {
            var activeCategoryIds = await db.Categories.AsNoTracking()
                .Where(x => categoryRateIds.Contains(x.CategoryId) && x.Status == "active")
                .Select(x => x.CategoryId).ToListAsync(ct);
            if (activeCategoryIds.Count != categoryRates.Count)
                throw Error(400, "CATEGORY_NOT_ACTIVE", "Có danh mục mô phỏng không hoạt động hoặc không tồn tại.");
        }
        if (request.PreferredCategoryId.HasValue && !await db.Categories.AsNoTracking()
                .AnyAsync(x => x.CategoryId == request.PreferredCategoryId && x.Status == "active", ct))
            throw Error(400, "CATEGORY_NOT_ACTIVE", "Danh mục ưu tiên không hoạt động hoặc không tồn tại.");
        var users = await db.Users.AsNoTracking().Where(x => request.UserIds.Contains(x.UserId) && x.Status == "active")
            .Select(x => new { x.UserId, x.Email }).ToListAsync(ct);
        if (users.Count != request.UserIds.Distinct().Count())
            throw Error(400, "USER_NOT_ACTIVE", "Danh sách có user không hoạt động hoặc không tồn tại.");
        var videos = await VideoAccessPolicy.Catalogue(db).Where(x => request.VideoIds.Contains(x.VideoId)
                && x.Status == "published" && x.ModerationStatus == "approved"
                && x.Visibility == "public" && x.Duration > 0)
            .Select(x => x.VideoId).ToListAsync(ct);
        if (videos.Count != request.VideoIds.Distinct().Count())
            throw Error(400, "VIDEO_NOT_PUBLIC", "Danh sách có video không công khai hoặc không tồn tại.");
        if (request.CategoryComments is null && request.CommentTemplates.Count > 0
            && await VideoAccessPolicy.Catalogue(db).Where(x => request.VideoIds.Contains(x.VideoId))
                .Select(x => x.CategoryId).Distinct().CountAsync(ct) > 1)
            throw Error(400, "INVALID_SIMULATION_COMMENTS", "Mô phỏng nhiều danh mục cần mẫu bình luận riêng theo danh mục. Vui lòng tải lại trang quản trị.");
        if (categoryRates.Count > 0)
        {
            var videoCategoryIds = await VideoAccessPolicy.Catalogue(db)
                .Where(x => request.VideoIds.Contains(x.VideoId) && x.CategoryId.HasValue)
                .Select(x => x.CategoryId!.Value).Distinct().ToListAsync(ct);
            if (categoryRates.Any(rate => !videoCategoryIds.Contains(rate.CategoryId)))
                throw Error(400, "CATEGORY_VIDEO_EMPTY", "Mỗi danh mục đã chọn cần có ít nhất một video trong danh sách mô phỏng.");
        }
        if (users.Any(x => !x.Email.EndsWith(".hutube.invalid", StringComparison.OrdinalIgnoreCase)) && !request.ConfirmRealUsers)
            throw Error(400, "CONFIRM_REAL_USERS", "Cần xác nhận rõ khi mô phỏng trên tài khoản thật.");
        var job = new RecommendationJob { ActorUserId = actorId, Kind = "simulation",
            PayloadJson = Serialize(request), Total = request.UserIds.Count * request.ActionsPerUser };
        db.RecommendationJobs.Add(job);
        db.AuditLogs.Add(new AuditLog { ActorUserId = actorId, Action = "recommendations.simulation_queued",
            ResourceType = "recommendation_job", ResourceId = job.JobId,
            NewValues = Serialize(new { users = request.UserIds, videos = request.VideoIds,
                request.Mode, request.ActionsPerUser, request.VideoSkipRate, categoryRates }), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        jobWakeup?.Signal();
        return job;
    }

    public async Task<RecommendationJob> JobAsync(Guid id, CancellationToken ct) =>
        await db.RecommendationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == id, ct)
        ?? throw Error(404, "JOB_NOT_FOUND", "Không tìm thấy job.");

    public async Task StopAsync(Guid id, CancellationToken ct)
    {
        var job = await db.RecommendationJobs.SingleOrDefaultAsync(x => x.JobId == id, ct)
            ?? throw Error(404, "JOB_NOT_FOUND", "Không tìm thấy job.");
        if (job.Status is "completed" or "failed" or "cancelled") return;
        if (job.Kind == "model_update")
        {
            // A queued job must be claimed atomically against the worker.
            if (db.Database.IsRelational() && job.Status == "queued")
            {
                var changed = await db.RecommendationJobs.Where(x => x.JobId == id && x.Status == "queued")
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "cancelled")
                        .SetProperty(x => x.Step, "cancelled").SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
                if (changed != 0) return;
                await db.Entry(job).ReloadAsync(ct);
            }
            if (job.Status == "queued")
            { job.Status = "cancelled"; job.Step = "cancelled"; await db.SaveChangesAsync(ct); return; }
            if (job.Status is "completed" or "failed" or "cancelled") return;
            if (string.IsNullOrWhiteSpace(options.ServiceUrl) || string.IsNullOrWhiteSpace(options.AdminToken))
                throw Error(503, "MODEL_SERVICE_UNAVAILABLE", "Không thể xác nhận hủy với dịch vụ model.");
            using var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Post, options.ServiceUrl.TrimEnd('/') +
                "/internal/model/jobs/" + Uri.EscapeDataString(job.ServiceJobId ?? job.JobId.ToString()) + "/cancel");
            request.Headers.Add("X-Model-Admin-Token", options.AdminToken);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                throw Error(409, "MODEL_JOB_FINALIZING", "Model đang hoàn tất publish; không thể hủy lượt này.");
            if (!response.IsSuccessStatusCode)
                throw Error(503, "MODEL_CANCEL_UNCONFIRMED", "Dịch vụ model chưa xác nhận hủy.");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (result.RootElement.GetProperty("status").GetString() != "cancelled")
                throw Error(409, "MODEL_JOB_ALREADY_FINISHED", "Job đã kết thúc trước khi nhận yêu cầu hủy.");
            if (db.Database.IsRelational())
                await db.RecommendationJobs.Where(x => x.JobId == id && (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling"))
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "cancelled")
                        .SetProperty(x => x.Step, "cancelled").SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            else { job.Status = "cancelled"; job.Step = "cancelled"; await db.SaveChangesAsync(ct); }
            return;
        }
        if (job.Status is "queued" or "running")
        { job.Status = "cancelling"; job.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); }
    }

    public async Task ProcessAsync(RecommendationJob job, CancellationToken ct)
    {
        try
        {
            if (job.Kind == "model_update") await EnsureModelJobActiveAsync(job, ct);
            if (job.Kind == "model_update") await ProcessModelAsync(job, ct);
            else await ProcessSimulatorAsync(job, ct);
            job.Status = job.Status == "cancelling" ? "cancelled" : "completed";
            job.Step = job.Status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (OperationCanceledException) when (job.Kind == "model_update")
        { job.Status = "cancelled"; job.Step = "cancelled"; job.Error = null; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recommendation job {JobId} failed at {Step}", job.JobId, job.Step);
            job.Status = "failed"; job.Error = $"{job.Step}: {ex.Message}";
        }
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task ProcessModelAsync(RecommendationJob job, CancellationToken ct)
    {
        var scoreAggregation = ScoreAggregationFromPayload(job.PayloadJson);
        var modelAlgorithm = ModelAlgorithmFromPayload(job.PayloadJson);
        var key = job.ModelCsvKey;
        var hash = job.ModelCsvSha256;
        if (key == null || hash == null)
        {
        job.Step = "matrix"; await db.SaveChangesAsync(ct);
        var rows = await BuildMatrixAsync(ct);
        await EnsureModelJobActiveAsync(job, ct);
        if (rows.Count < 3 || rows.Select(x => x.UserId).Distinct().Count() < 2 || rows.Select(x => x.VideoId).Distinct().Count() < 2)
            throw Error(422, "MATRIX_TOO_SMALL", "CF cần ít nhất 2 user, 2 video và 3 cặp tương tác.");
        var bytes = ToCsv(rows, scoreAggregation);
        if (bytes.Length > 32 * 1024 * 1024)
            throw Error(422, "MATRIX_TOO_LARGE", "CSV vượt giới hạn 32 MiB của Render Free.");
        hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        key = $"collaborative_cf/{now:yyyy/MM/dd}/interactions_{now:yyyyMMddTHHmmssZ}_{hash[..12]}.csv";
        job.Step = "upload_csv"; await db.SaveChangesAsync(ct);
        await snapshots.WriteAsync(key, bytes, "text/csv", ct);
        job.ModelCsvKey = key; job.ModelCsvSha256 = hash;
        await db.SaveChangesAsync(ct);
        }
        job.ServiceJobId ??= job.JobId.ToString();
        await db.SaveChangesAsync(ct);
        job.Step = "train_model"; await db.SaveChangesAsync(ct);
        await EnsureModelJobActiveAsync(job, ct);
        if (string.IsNullOrWhiteSpace(options.ServiceUrl) || string.IsNullOrWhiteSpace(options.AdminToken))
            throw new InvalidOperationException("Recommendation Service URL/AdminToken is not configured.");
        using var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var serviceJobId = await new RecommendationModelClient(client, options, logger)
            .SubmitAsync(job.ServiceJobId, key, hash, scoreAggregation, modelAlgorithm, ct);
        job.ServiceJobId = serviceJobId;
        await db.SaveChangesAsync(ct);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await EnsureModelJobActiveAsync(job, ct);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            using var poll = new HttpRequestMessage(HttpMethod.Get,
                options.ServiceUrl.TrimEnd('/') + "/internal/model/jobs/" + Uri.EscapeDataString(serviceJobId));
            poll.Headers.Add("X-Model-Admin-Token", options.AdminToken);
            HttpResponseMessage progress;
            try { progress = await client.SendAsync(poll, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                var reconciled = await ActiveAsync(ct);
                if (MatchesPublishedModel(reconciled, key, hash, scoreAggregation, modelAlgorithm)) break;
                continue;
            }
            using var progressLifetime = progress;
            var progressBody = await progress.Content.ReadAsStringAsync(ct);
            if (!progress.IsSuccessStatusCode)
            {
                if (progress.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("MODEL_ADMIN_TOKEN_MISMATCH: Recommendation__AdminToken của API phải trùng RECOMMENDER_ADMIN_TOKEN của Recommendation Service. Kiểm tra cấu hình và khởi động lại cả hai dịch vụ.");
                var reconciled = await ActiveAsync(ct);
                if (MatchesPublishedModel(reconciled, key, hash, scoreAggregation, modelAlgorithm)) break;
                if (progress.StatusCode == System.Net.HttpStatusCode.NotFound || (int)progress.StatusCode >= 500) continue;
                throw RecommendationModelClient.Failure(progress.StatusCode, progressBody);
            }
            using var progressJson = JsonDocument.Parse(progressBody);
            var state = progressJson.RootElement.GetProperty("status").GetString();
            if (state == "cancelled") throw new OperationCanceledException("Model job cancelled.");
            if (state == "completed") break;
            if (state == "failed")
                throw new InvalidOperationException(progressJson.RootElement.GetProperty("error").GetString() ?? "Model training failed.");
        }
        if (DateTimeOffset.UtcNow >= deadline)
            throw new TimeoutException("Model training exceeded the 20-minute job limit.");
        job.Step = "verify_manifest"; await db.SaveChangesAsync(ct);
        var active = await ActiveAsync(ct);
        if (!MatchesPublishedModel(active, key, hash, scoreAggregation, modelAlgorithm))
            throw new InvalidOperationException("Recommendation Service did not publish the requested CSV manifest.");
        var publishedModel = active!;
        var cleanupMessage = "Các CSV ma trận cũ đã được dọn khỏi R2.";
        try
        {
            await snapshots.DeleteCsvExceptAsync(key, ct);
        }
        catch (Exception cleanupError)
        {
            // The new manifest is already authoritative. A cleanup failure must not
            // roll the model back; the next successful update can retry the sweep.
            logger.LogWarning(cleanupError, "Could not remove old recommendation CSV snapshots after model {ModelVersion}.", publishedModel.ModelVersion);
            cleanupMessage = "Không thể dọn toàn bộ CSV cũ trên R2; model mới vẫn đang hoạt động và lượt cập nhật sau sẽ thử lại.";
        }
        job.LogsJson = Serialize(new[] {
            $"Model {publishedModel.ModelVersion} đang hoạt động.",
            $"CSV: {key}",
            $"SHA-256: {hash}",
            $"Model algorithm: {modelAlgorithm}.",
            $"Score aggregation: {scoreAggregation.Mode}.",
            cleanupMessage
        });
    }

    private async Task EnsureModelJobActiveAsync(RecommendationJob job, CancellationToken ct)
    {
        var state = await db.RecommendationJobs.AsNoTracking().Where(x => x.JobId == job.JobId)
            .Select(x => x.Status).SingleAsync(ct);
        if (state is "cancelled" or "cancelling") throw new OperationCanceledException("Model update was cancelled.");
    }

    private async Task ProcessSimulatorAsync(RecommendationJob job, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<SimulatorRequest>(job.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Invalid simulation payload.");
        var videos = await VideoAccessPolicy.Catalogue(db).Where(x => request.VideoIds.Contains(x.VideoId))
            .Select(x => new SimulationVideo(x.VideoId, x.ChannelId, x.UploadedByUserId, x.Duration, x.CategoryId)).ToListAsync(ct);
        var videosById = videos.ToDictionary(x => x.VideoId);
        var commentsByCategory = (request.CategoryComments ?? []).ToDictionary(x => x.CategoryId, x => x.Templates);
        // Old queued jobs only have one shared bank. It is safe for a single category;
        // mixed-category jobs must never reuse it across unrelated videos.
        var legacyComments = request.CategoryComments is null && videos.Select(x => x.CategoryId).Distinct().Count() == 1
            ? request.CommentTemplates : [];
        var videoChannelIds = videos.Select(video => video.ChannelId).Distinct().ToArray();
        var channelOwners = await db.Channels.AsNoTracking()
            .Where(x => videoChannelIds.Contains(x.ChannelId))
            .ToDictionaryAsync(x => x.ChannelId, x => x.OwnerUserId, ct);
        var userIds = request.UserIds.Distinct().ToArray();
        var videoIds = request.VideoIds.Distinct().ToArray();
        var reactionByPair = (await db.VideoReactions.AsNoTracking()
            .Where(x => userIds.Contains(x.UserId) && videoIds.Contains(x.VideoId))
            .OrderByDescending(x => x.UpdatedAt).ToListAsync(ct))
            .GroupBy(x => (x.UserId, x.VideoId))
            .ToDictionary(group => group.Key, group => group.First().Type);
        var ratingByPair = (await db.VideoRatings.AsNoTracking()
            .Where(x => userIds.Contains(x.UserId) && videoIds.Contains(x.VideoId))
            .OrderByDescending(x => x.UpdatedAt).ToListAsync(ct))
            .GroupBy(x => (x.UserId, x.VideoId))
            .ToDictionary(group => group.Key, group => (int)group.First().Score);
        var channelIds = videos.Select(x => x.ChannelId).Distinct().ToArray();
        var subscribedChannels = (await db.Subscriptions.AsNoTracking()
            .Where(x => userIds.Contains(x.UserId) && channelIds.Contains(x.ChannelId) && x.Status == "active")
            .Select(x => new { x.UserId, x.ChannelId }).ToListAsync(ct))
            .Select(x => (x.UserId, x.ChannelId)).ToHashSet();
        var categoryRates = request.CategoryRates ?? [];
        if (categoryRates.Count > 0 && categoryRates.Any(rate => !videos.Any(video => video.CategoryId == rate.CategoryId)))
            throw Error(400, "CATEGORY_VIDEO_EMPTY", "Mỗi danh mục đã chọn cần có ít nhất một video trong danh sách mô phỏng.");
        var logs = new List<string>();
        job.Step = "interactions"; await db.SaveChangesAsync(ct);
        var checkpointActions = 0;
        foreach (var userId in userIds)
        {
            var plan = categoryRates.Count > 0
                ? BuildWeightedCategoryPlan(videos, request.ActionsPerUser, categoryRates)
                : request.Mode == "cluster"
                    ? BuildPersonaPlan(videos, request.ActionsPerUser, request.PreferredCategoryId, request.PreferredCategoryRatio)
                    : [];
            for (var i = 0; i < request.ActionsPerUser; i++)
            {
                if (checkpointActions == 0)
                {
                    await db.Entry(job).ReloadAsync(ct);
                    if (job.Status == "cancelling") return;
                }
                var video = categoryRates.Count > 0
                    ? plan[i]
                    : request.Mode == "target"
                        ? videosById[request.VideoIds[i % request.VideoIds.Count]]
                        : plan[i];
                var pair = (userId, video.VideoId);
                var isOwnContent = video.UploadedByUserId == userId
                    || (channelOwners.TryGetValue(video.ChannelId, out var channelOwner) && channelOwner == userId);
                async Task<bool> Act(string behavior, Func<Task> action, string? detail = null)
                {
                    try
                    {
                        await action();
                        db.AuditLogs.Add(new AuditLog { ActorUserId = job.ActorUserId,
                            Action = "recommendations.simulated_" + behavior,
                            ResourceType = "video", ResourceId = video.VideoId,
                            NewValues = Serialize(new { targetUserId = userId, videoId = video.VideoId, behavior, jobId = job.JobId }),
                            CreatedAt = DateTimeOffset.UtcNow });
                        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
                        logs.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {userId} {behavior} {video.VideoId}{suffix}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        logs.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {userId} {behavior}: {ex.Message}");
                        return false;
                    }
                }
                // A skipped video is a deliberate absence of interaction. Keep the
                // attempt visible in the simulator stream/audit trail, but do not
                // write viewing history or any other signal for this video.
                if (Hit(request.VideoSkipRate))
                {
                    await Act("skip", () => Task.CompletedTask);
                    job.Completed++;
                    checkpointActions++;
                    if (checkpointActions >= 25 || job.Completed >= job.Total)
                    {
                        job.LogsJson = Serialize(logs.TakeLast(500).ToArray());
                        job.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct);
                        checkpointActions = 0;
                    }
                    if (request.DelayMs > 0) await Task.Delay(request.DelayMs, ct);
                    continue;
                }
                if (Hit(request.ViewRate))
                {
                    // A watched video gets a fresh completion percentage for this
                    // attempt. A single fixed value would make every simulated
                    // account look unnaturally identical in the interaction data.
                    var watchPercent = Random.Shared.Next(1, 101);
                    await Act("view", async () =>
                    {
                        await content.SaveProgressAsync(userId, video.VideoId,
                            new WatchProgressRequest(Math.Clamp(
                                (int)Math.Round(video.Duration * watchPercent / 100.0), 1, video.Duration),
                                true, true), ct);
                    }, $"{watchPercent}%");
                }
                var reaction = reactionByPair.TryGetValue(pair, out var existingReaction) ? existingReaction : null;
                var hadReaction = reaction is not null;
                if (!isOwnContent && reaction is null)
                {
                    var existingRating = ratingByPair.TryGetValue(pair, out var savedRating) ? savedRating : (int?)null;
                    var reactionRoll = Random.Shared.Next(100);
                    var candidate = reactionRoll < request.LikeRate ? "like"
                        : reactionRoll < request.LikeRate + request.DislikeRate ? "dislike" : null;
                    var compatible = candidate is null || !existingRating.HasValue
                        || (candidate == "like" && existingRating.Value >= 4)
                        || (candidate == "dislike" && existingRating.Value <= 2);
                    if (candidate == "like" && compatible)
                    {
                        if (await Act("like", async () => await content.SetVideoReactionAsync(userId, video.VideoId, "like", ct)))
                            reactionByPair[pair] = reaction = "like";
                    }
                    else if (candidate == "dislike" && compatible)
                    {
                        if (await Act("dislike", async () => await content.SetVideoReactionAsync(userId, video.VideoId, "dislike", ct)))
                            reactionByPair[pair] = reaction = "dislike";
                    }
                }
                // A dislike represents a zero-rating choice, so it never creates a
                // contradictory numeric rating. Neutral feedback uses three stars;
                // a skipped rating is the domain's zero-rating state.
                if (!isOwnContent && !hadReaction && !ratingByPair.ContainsKey(pair)
                    && reaction != "dislike" && Hit(request.RatingRate))
                {
                    var rating = reaction == "like" ? Random.Shared.Next(4, 6) : 3;
                    if (await Act("rating", async () => await content.SetRatingAsync(userId, video.VideoId, rating, ct)))
                        ratingByPair[pair] = rating;
                }
                var commentTemplates = video.CategoryId is { } categoryId && commentsByCategory.TryGetValue(categoryId, out var templates)
                    ? templates : legacyComments;
                if (Hit(request.CommentRate) && commentTemplates.Count > 0)
                    await Act("comment", async () => { await content.CreateCommentAsync(userId, video.VideoId,
                        new CreateCommentRequest(commentTemplates[Random.Shared.Next(commentTemplates.Count)], null), ct); });
                var subscriptionKey = (userId, video.ChannelId);
                var hasNegativeRating = ratingByPair.TryGetValue(pair, out var currentRating) && currentRating <= 2;
                if (!isOwnContent && !hadReaction && reaction != "dislike"
                    && !hasNegativeRating && !subscribedChannels.Contains(subscriptionKey) && Hit(request.SubscribeRate)
                    && await Act("subscribe", async () => { await channels.SubscribeAsync(video.ChannelId, userId, ct); }))
                    subscribedChannels.Add(subscriptionKey);
                job.Completed++;
                checkpointActions++;
                if (checkpointActions >= 25 || job.Completed >= job.Total)
                {
                    job.LogsJson = Serialize(logs.TakeLast(500).ToArray());
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    checkpointActions = 0;
                }
                if (request.DelayMs > 0) await Task.Delay(request.DelayMs, ct);
            }
        }
        if (checkpointActions > 0)
        {
            job.LogsJson = Serialize(logs.TakeLast(500).ToArray());
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static List<SimulationVideo> BuildWeightedCategoryPlan(
        IReadOnlyList<SimulationVideo> videos, int count, IReadOnlyList<SimulationCategoryRate> rates)
    {
        var definitions = rates.Select(rate => new { rate.CategoryId, rate.Rate }).ToList();
        Dictionary<Guid, List<SimulationVideo>> CreatePools() => definitions.ToDictionary(
            item => item.CategoryId,
            item => videos.Where(video => video.CategoryId == item.CategoryId)
                .OrderBy(_ => Random.Shared.Next()).ToList());
        var pools = CreatePools();
        if (pools.Values.Any(items => items.Count == 0))
            return BuildPersonaPlan(videos, count, null, 0);
        var plan = new List<SimulationVideo>(count);
        while (plan.Count < count)
        {
            var available = definitions.ToList();
            var total = available.Sum(item => item.Rate);
            var roll = Random.Shared.Next(total);
            var selected = available[0];
            foreach (var item in available)
            {
                if (roll < item.Rate) { selected = item; break; }
                roll -= item.Rate;
            }
            var pool = pools[selected.CategoryId];
            if (pool.Count == 0)
                pool.AddRange(videos.Where(video => video.CategoryId == selected.CategoryId).OrderBy(_ => Random.Shared.Next()));
            plan.Add(pool[^1]);
            pool.RemoveAt(pool.Count - 1);
        }
        return plan;
    }

    private static List<SimulationVideo> BuildPersonaPlan(IReadOnlyList<SimulationVideo> videos, int count,
        Guid? preferredCategoryId, int preferredRatio)
    {
        var preferred = preferredCategoryId.HasValue
            ? videos.Where(video => video.CategoryId == preferredCategoryId).ToList()
            : new List<SimulationVideo>();
        var other = preferredCategoryId.HasValue
            ? videos.Where(video => video.CategoryId != preferredCategoryId).ToList()
            : videos.ToList();
        static List<SimulationVideo> Shuffle(List<SimulationVideo> source) => source.OrderBy(_ => Random.Shared.Next()).ToList();
        var primary = Shuffle(preferred);
        var secondary = Shuffle(other);
        var preferredCount = preferredCategoryId.HasValue ? (int)Math.Round(count * preferredRatio / 100.0) : 0;
        var plan = primary.Take(preferredCount).Concat(secondary.Take(count - preferredCount)).ToList();
        if (plan.Count < count)
            plan.AddRange(primary.Skip(Math.Min(preferredCount, primary.Count)).Concat(secondary.Skip(Math.Max(0, count - preferredCount))).Take(count - plan.Count));
        var fallback = Shuffle(videos.ToList());
        while (plan.Count < count) plan.Add(fallback[Random.Shared.Next(fallback.Count)]);
        return plan;
    }

    private static bool Hit(int rate) => rate > 0 && Random.Shared.Next(100) < rate;
}
