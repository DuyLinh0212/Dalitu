using System.Security.Claims;
using System.Text.Json;
using HuTube.Api.RateLimiting;
using HuTube.Application.Videos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HuTube.Api.Controllers;

[ApiController, Route("api/v1/categories")]
public sealed class CategoriesController(IContentService content) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<CategoryResponse>> GetAsync(CancellationToken ct) => content.GetCategoriesAsync(ct);
}

[ApiController, Route("api/v1/feed"), EnableRateLimiting("feed")]
public sealed class FeedController(IContentService content) : ControllerBase
{
    // FastAPI reads authenticated context and resolves ranked IDs without invoking CF again.
    [Authorize, HttpGet("recommendation-context")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<RecommendationContextResponse> RecommendationContextAsync(CancellationToken ct) =>
        content.GetRecommendationContextAsync(Guid.Parse(User.FindFirst("sub")!.Value), ct);

    [Authorize, HttpPost("resolve")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<PageResult<VideoCardResponse>> ResolveAsync(ResolveFeedRequest request, CancellationToken ct) =>
        content.ResolveFeedAsync(Guid.Parse(User.FindFirst("sub")!.Value), request.VideoIds, request.Page, request.PageSize, ct);

    [HttpGet("home")]
    public Task<PageResult<VideoCardResponse>> HomeAsync([FromQuery] string sort = "popular", [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var subject = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var currentUserId = Guid.TryParse(subject, out var uid) ? uid : (Guid?)null;
        return content.GetFeedAsync("home", sort, null, null, page, pageSize, currentUserId, ct);
    }

    [HttpGet("recommended")]
    public Task<PageResult<VideoCardResponse>> RecommendedAsync([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var subject = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var currentUserId = Guid.TryParse(subject, out var uid) ? uid : (Guid?)null;
        return content.GetFeedAsync("home", "recommended", null, null, page, pageSize, currentUserId, ct);
    }

    [HttpGet("explore-hub")]
    public Task<ExploreHubResponse> ExploreHubAsync(CancellationToken ct = default) =>
        content.GetExploreHubAsync(ct);

    [HttpGet("explore")]
    public Task<PageResult<VideoCardResponse>> ExploreAsync(
        [FromQuery] string? q = null,
        [FromQuery] string sort = "newest",
        [FromQuery] Guid? categoryId = null,
        [FromQuery] string? tag = null,
        [FromQuery] Guid? channelId = null,
        [FromQuery] string? dateRange = null,
        [FromQuery] string? duration = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        content.SearchVideosAsync(new SearchVideosQuery(q, categoryId, tag, channelId, dateRange, duration, sort, page, pageSize), ct);

    [HttpGet("subscriptions"), Authorize]
    public Task<PageResult<VideoCardResponse>> SubscriptionsAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var subject = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out var userId))
            throw new ContentException(401, "UNAUTHORIZED", "Vui lòng đăng nhập để tiếp tục.");
        return content.GetSubscriptionsFeedAsync(userId, page, pageSize, ct);
    }
}

[ApiController, Route("api/v1/library"), Authorize]
public sealed class LibraryController(IContentService content) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
        ? id
        : throw new ContentException(401, "UNAUTHORIZED", "Vui lòng đăng nhập để tiếp tục.");

    [HttpGet("history")]
    public Task<PageResult<WatchHistoryResponse>> HistoryAsync([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] DateTimeOffset? before = null, [FromQuery] string? q = null,
        [FromQuery] string sort = "newest", [FromQuery] string? dateRange = null, CancellationToken ct = default) =>
        content.GetWatchHistoryAsync(UserId, page, pageSize, before, ct, q, sort, dateRange);

    [HttpDelete("history")]
    public async Task<IActionResult> ClearHistoryAsync(CancellationToken ct = default)
    {
        await content.ClearWatchHistoryAsync(UserId, ct);
        return NoContent();
    }

    [HttpDelete("history/{videoId:guid}")]
    public async Task<IActionResult> DeleteHistoryItemAsync(Guid videoId, CancellationToken ct = default)
    {
        await content.DeleteWatchHistoryItemAsync(UserId, videoId, ct);
        return NoContent();
    }

    [HttpGet("liked")]
    public Task<PageResult<LibraryVideoResponse>> LikedAsync([FromQuery] int? rating = null, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        content.GetLikedVideosAsync(UserId, rating, page, pageSize, ct);
}

public sealed class UploadVideoForm
{
    public Guid ChannelId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public Guid? CategoryId { get; set; }
    public string? LanguageCode { get; set; }
    public string Visibility { get; set; } = "private";
    public bool AllowComments { get; set; } = true;
    public bool AgeRestricted { get; set; }
    public int Duration { get; set; }
    public string SourceQuality { get; set; } = "720p";
    public IFormFile? Video { get; set; }
    public IFormFile? Thumbnail { get; set; }
    public string[] Tags { get; set; } = [];
    public string? ChaptersJson { get; set; }
    public string? VideoCardsJson { get; set; }
}

public sealed class UpdateThumbnailForm
{
    public IFormFile? Thumbnail { get; set; }
    public bool Generate { get; set; }
}

[ApiController, Route("api/v1/videos")]
public sealed class VideosController(IContentService content) : ControllerBase
{
    private Guid? CurrentUserId => Guid.TryParse(User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private Guid UserId => CurrentUserId ?? throw new ContentException(401, "UNAUTHORIZED", "Vui lòng đăng nhập để tiếp tục.");

    [HttpGet("search/channels"), EnableRateLimiting("search")]
    public Task<IReadOnlyList<SearchChannelResponse>> SearchChannelsAsync(
        [FromQuery] string? q = null,
        [FromQuery] Guid? categoryId = null,
        [FromQuery] string? dateRange = null,
        [FromQuery] string? duration = null,
        CancellationToken ct = default) =>
        content.GetSearchChannelsAsync(new SearchVideosQuery(Query: q, CategoryId: categoryId, DateRange: dateRange, DurationRange: duration), ct);

    [HttpGet("search"), EnableRateLimiting("search")]
    public Task<PageResult<VideoCardResponse>> SearchAsync(
        [FromQuery] string? q = null,
        [FromQuery] Guid? categoryId = null,
        [FromQuery] string? tag = null,
        [FromQuery] Guid? channelId = null,
        [FromQuery] string? dateRange = null,
        [FromQuery] string? duration = null,
        [FromQuery] string? sort = "relevance",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        content.SearchVideosAsync(new SearchVideosQuery(q, categoryId, tag, channelId, dateRange, duration, sort, page, pageSize), ct);

    [HttpGet("{id:guid}")]
    public Task<VideoResponse> GetAsync(Guid id, CancellationToken ct) => content.GetVideoAsync(id, CurrentUserId, ct);

    [HttpGet("{id:guid}/playback"), EnableRateLimiting("playback")]
    public Task<PlaybackResponse> PlaybackAsync(Guid id, CancellationToken ct) => content.GetPlaybackAsync(id, CurrentUserId, ct);

    [Authorize, HttpPost, DisableRequestSizeLimit, EnableRateLimiting("upload")]
    public async Task<ActionResult<VideoResponse>> UploadAsync([FromForm] UploadVideoForm form, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (form.Video == null || form.Video.Length == 0) throw new ContentException(400, "VIDEO_REQUIRED", "Vui lòng chọn file video.");
        IReadOnlyList<ChapterRequest> chapters = [];
        IReadOnlyList<VideoCardRequest> videoCards = [];
        if (!string.IsNullOrWhiteSpace(form.ChaptersJson))
        {
            try { chapters = JsonSerializer.Deserialize<List<ChapterRequest>>(form.ChaptersJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
            catch (JsonException) { throw new ContentException(400, "INVALID_CHAPTERS", "Danh sách chương không đúng định dạng JSON."); }
        }
        if (!string.IsNullOrWhiteSpace(form.VideoCardsJson))
        {
            try { videoCards = JsonSerializer.Deserialize<List<VideoCardRequest>>(form.VideoCardsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
            catch (JsonException) { throw new ContentException(400, "INVALID_VIDEO_CARDS", "Danh sách video liên quan không đúng định dạng JSON."); }
        }
        await using var videoStream = form.Video.OpenReadStream();
        await using var thumbnailStream = form.Thumbnail?.OpenReadStream();
        var result = await content.CreateVideoAsync(UserId, new CreateVideoCommand(form.ChannelId, form.Title, form.Description,
            form.CategoryId, form.LanguageCode, form.Visibility, form.AgeRestricted, form.Duration, form.SourceQuality, form.Video.Length,
            form.Video.FileName, form.Video.ContentType, videoStream, form.Thumbnail?.FileName, form.Thumbnail?.ContentType,
            thumbnailStream, form.Tags, chapters, idempotencyKey, DeferLowerRenditions: true, AllowComments: form.AllowComments, VideoCards: videoCards), ct);
        return Created($"/api/v1/videos/{result.VideoId}", result);
    }

    [Authorize, HttpPost("upload-preflight"), EnableRateLimiting("upload-preflight")]
    public Task<UploadPreflightResponse> PreflightAsync(UploadPreflightRequest request, CancellationToken ct) => content.PreflightAsync(UserId, request, ct);

    [Authorize, HttpGet("manage")]
    public Task<PageResult<VideoResponse>> ManageAsync([FromQuery] Guid channelId, [FromQuery] string? status = null,
        [FromQuery] string? visibility = null, [FromQuery] string? search = null, [FromQuery] Guid? categoryId = null,
        [FromQuery] string? tag = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        content.GetManagedVideosAsync(UserId, channelId, status, visibility, search, categoryId, tag, page, pageSize, ct);

    [Authorize, HttpPatch("{id:guid}")]
    public Task<VideoResponse> UpdateAsync(Guid id, UpdateVideoRequest request, CancellationToken ct) => content.UpdateVideoAsync(UserId, id, request, ct);

    [Authorize, HttpPost("{id:guid}/thumbnail")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<VideoResponse> UpdateThumbnailAsync(Guid id, [FromForm] UpdateThumbnailForm form, CancellationToken ct)
    {
        if (!form.Generate && (form.Thumbnail == null || form.Thumbnail.Length == 0))
            throw new ContentException(400, "THUMBNAIL_REQUIRED", "Vui lòng chọn thumbnail hoặc bật tự động tạo thumbnail.");
        if (form.Thumbnail is { Length: > 5 * 1024 * 1024 })
            throw new ContentException(400, "FILE_TOO_LARGE", "Kích thước thumbnail tối đa là 5MB.");

        var allowed = new HashSet<string>(["image/jpeg", "image/png", "image/webp"], StringComparer.OrdinalIgnoreCase);
        if (form.Thumbnail != null && !allowed.Contains(form.Thumbnail.ContentType))
            throw new ContentException(400, "INVALID_FILE_TYPE", "Thumbnail chỉ hỗ trợ JPG, PNG hoặc WEBP.");

        await using var stream = form.Thumbnail?.OpenReadStream();
        return await content.UpdateThumbnailAsync(UserId, id,
            new UpdateThumbnailRequest(stream, form.Thumbnail?.FileName, form.Thumbnail?.ContentType, form.Generate), ct);
    }

    [Authorize, HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct) { await content.DeleteVideoAsync(UserId, id, ct); return NoContent(); }

    [Authorize, HttpPost("{id:guid}/submit-moderation")]
    public Task<VideoResponse> SubmitAsync(Guid id, CancellationToken ct) => content.SubmitModerationAsync(UserId, id, ct);

    [Authorize, HttpPost("{id:guid}/publish")]
    public Task<VideoResponse> PublishAsync(Guid id, CancellationToken ct) => content.PublishAsync(UserId, id, ct);

    [Authorize, HttpPost("{id:guid}/cancel-upload")]
    public async Task<IActionResult> CancelUploadAsync(Guid id, CancellationToken ct) { await content.CancelUploadAsync(UserId, id, ct); return NoContent(); }

    [Authorize, HttpPost("{id:guid}/retry-processing")]
    public Task<VideoResponse> RetryProcessingAsync(Guid id, CancellationToken ct) => content.RetryProcessingAsync(UserId, id, ct);

    [Authorize, HttpPut("{id:guid}/watch-progress")]
    public Task<WatchProgressResponse> ProgressAsync(Guid id, WatchProgressRequest request, CancellationToken ct) => content.SaveProgressAsync(UserId, id, request, ct);

    [Authorize, HttpPut("{id:guid}/reaction")]
    public Task<ReactionResponse> ReactAsync(Guid id, ReactionRequest request, CancellationToken ct) => content.SetVideoReactionAsync(UserId, id, request.Type, ct);

    [Authorize, HttpDelete("{id:guid}/reaction")]
    public Task<ReactionResponse> RemoveReactionAsync(Guid id, CancellationToken ct) => content.SetVideoReactionAsync(UserId, id, null, ct);

    [Authorize, HttpPut("{id:guid}/rating")]
    public Task<RatingResponse> RateAsync(Guid id, RatingRequest request, CancellationToken ct) => content.SetRatingAsync(UserId, id, request.Score, ct);

    [Authorize, HttpDelete("{id:guid}/rating")]
    public Task<RatingResponse> RemoveRatingAsync(Guid id, CancellationToken ct) => content.SetRatingAsync(UserId, id, null, ct);

    [Authorize, HttpPost("{id:guid}/share")]
    public Task<ShareResponse> ShareAsync(Guid id, ShareRequest request, CancellationToken ct) => content.ShareAsync(UserId, id, request.Method, ct);

    [Authorize, HttpGet("{id:guid}/download-options")]
    public async Task<IReadOnlyList<RenditionResponse>> DownloadOptionsAsync(Guid id, CancellationToken ct)
    {
        var options = await content.GetDownloadOptionsAsync(UserId, id, ct);
        return Request.Headers["X-HuTube-Client"] == "web"
            ? options.Select(x => x with { Url = "" }).ToArray() : options;
    }

    [Authorize, HttpPost("{id:guid}/download-file")]
    public async Task<IActionResult> DownloadFileAsync(Guid id, CreateDownloadRequest request, CancellationToken ct)
    {
        var file = await content.OpenDownloadFileAsync(UserId, id, request.Quality, ct);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    [Authorize, HttpPost("{id:guid}/downloads")]
    public async Task<ActionResult<DownloadResponse>> DownloadAsync(Guid id, CreateDownloadRequest request, CancellationToken ct)
    {
        var result = await content.CreateDownloadAsync(UserId, id, request.Quality, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }
}

[ApiController, Route("api/v1")]
public sealed class CommentsController(IContentService content, HuTube.Infrastructure.Videos.ReportService reports) : ControllerBase
{
    private Guid? CurrentUserId => Guid.TryParse(User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private Guid UserId => CurrentUserId ?? throw new ContentException(401, "UNAUTHORIZED", "Vui lòng đăng nhập để tiếp tục.");

    [HttpGet("videos/{videoId:guid}/comments")]
    public Task<PageResult<CommentResponse>> GetAsync(Guid videoId, [FromQuery] string sort = "newest", [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) => content.GetCommentsAsync(videoId, CurrentUserId, sort, page, pageSize, ct);

    [Authorize, HttpPost("videos/{videoId:guid}/comments"), EnableRateLimiting(RateLimitOptions.CommentCreationPolicy)]
    public async Task<ActionResult<CommentResponse>> CreateAsync(Guid videoId, CreateCommentRequest request, CancellationToken ct)
    {
        var result = await content.CreateCommentAsync(UserId, videoId, request, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpGet("comments/{id:guid}/replies")]
    public Task<PageResult<CommentResponse>> RepliesAsync(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) => content.GetRepliesAsync(id, CurrentUserId, page, pageSize, ct);

    [Authorize, HttpGet("channels/{channelId:guid}/comments/manage")]
    public Task<PageResult<CommentResponse>> ManageAsync(Guid channelId, [FromQuery] string? status = null, [FromQuery] string? sort = "newest", [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        content.GetManagedCommentsAsync(UserId, channelId, status, sort, page, pageSize, ct);

    [Authorize, HttpPatch("comments/{id:guid}")]
    public Task<CommentResponse> UpdateAsync(Guid id, UpdateCommentRequest request, CancellationToken ct) => content.UpdateCommentAsync(UserId, id, request, ct);

    [Authorize, HttpDelete("comments/{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct) { await content.DeleteCommentAsync(UserId, id, ct); return NoContent(); }

    [Authorize, HttpPut("comments/{id:guid}/reaction")]
    public Task<ReactionResponse> ReactAsync(Guid id, ReactionRequest request, CancellationToken ct) => content.SetCommentReactionAsync(UserId, id, request.Type, ct);

    [Authorize, HttpDelete("comments/{id:guid}/reaction")]
    public Task<ReactionResponse> RemoveReactionAsync(Guid id, CancellationToken ct) => content.SetCommentReactionAsync(UserId, id, null, ct);

    [Authorize, HttpPost("comments/{id:guid}/report")]
    public async Task<ActionResult<ReportResponse>> ReportAsync(Guid id, ReportCommentRequest request, CancellationToken ct)
    {
        var result = await reports.CreateReportAsync(UserId, new CreateContentReportRequest("comment", id, request.ViolationTypeId, request.Description ?? ""), ct);
        return StatusCode(StatusCodes.Status201Created, new ReportResponse(result.ReportId, result.Status, result.CreatedAt));
    }

    [Authorize, HttpPut("comments/{id:guid}/pin")]
    public Task<CommentResponse> PinAsync(Guid id, CommentCreatorStateRequest request, CancellationToken ct) =>
        content.SetCommentCreatorStateAsync(UserId, id, request.Enabled, pin: true, ct);

    [Authorize, HttpPut("comments/{id:guid}/creator-heart")]
    public Task<CommentResponse> CreatorHeartAsync(Guid id, CommentCreatorStateRequest request, CancellationToken ct) =>
        content.SetCommentCreatorStateAsync(UserId, id, request.Enabled, pin: false, ct);

    [Authorize, HttpPatch("comments/{id:guid}/visibility")]
    public Task<CommentResponse> VisibilityAsync(Guid id, [FromBody] CommentVisibilityRequest request, CancellationToken ct) => content.SetCommentHiddenAsync(UserId, id, request.Hidden, request.Reason, ct);
}

[ApiController, Route("api/v1/violation-types")]
public sealed class ViolationTypesController(IContentService content) : ControllerBase
{
    [HttpGet] public Task<IReadOnlyList<ViolationTypeResponse>> GetAsync(CancellationToken ct) => content.GetViolationTypesAsync(ct);
}

public sealed record CommentVisibilityRequest(bool Hidden, string? Reason);

[ApiController, Route("api/v1/downloads"), Authorize]
public sealed class DownloadsController(IContentService content) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new ContentException(401, "UNAUTHORIZED", "Vui lòng đăng nhập để tiếp tục."));
    [HttpGet] public Task<IReadOnlyList<DownloadResponse>> GetAsync(CancellationToken ct) => content.GetDownloadsAsync(UserId, ct);
    [HttpDelete("{id:guid}")] public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct) { await content.DeleteDownloadAsync(UserId, id, ct); return NoContent(); }
    [HttpPost("{id:guid}/{operation:regex(^(pause|resume|retry|cancel)$)}")]
    public Task<DownloadResponse> StateAsync(Guid id, string operation, CancellationToken ct) => content.SetDownloadStateAsync(UserId, id, operation, ct);
    [HttpDelete]
    public async Task<IActionResult> DeleteManyAsync([FromBody] DeleteDownloadsRequest request, CancellationToken ct) { await content.DeleteDownloadsAsync(UserId, request.Ids, ct); return NoContent(); }
}

public sealed record DeleteDownloadsRequest(IReadOnlyList<Guid> Ids);
