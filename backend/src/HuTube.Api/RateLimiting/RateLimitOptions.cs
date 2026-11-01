using System.Threading.RateLimiting;

namespace HuTube.Api.RateLimiting;

public sealed class RateLimitOptions
{
    public const string CommentCreationPolicy = "comment-create";

    public int WindowSeconds { get; init; } = 60;
    public int AuthPermitLimit { get; init; } = 60;
    public int FeedPermitLimit { get; init; } = 90;
    public int SearchPermitLimit { get; init; } = 60;
    public int PlaybackPermitLimit { get; init; } = 180;
    public int UploadPermitLimit { get; init; } = 8;
    public int UploadPreflightPermitLimit { get; init; } = 24;
    public int UploadChunkPermitLimit { get; init; } = 300;
    public int UploadStatusPermitLimit { get; init; } = 120;
    public int PaymentInitiatePermitLimit { get; init; } = 12;
    public int PaymentWebhookPermitLimit { get; init; } = 120;
    public int AdminPermitLimit { get; init; } = 120;
    public int ModerationPermitLimit { get; init; } = 60;
    public int CommentPermitLimit { get; init; } = 2;
    public int CommentWindowSeconds { get; init; } = 2;
    public int CfSeederPermitLimit { get; init; } = 30;
    public int CfSeederUploadPermitLimit { get; init; } = 6;
    public int RecommendationJobPermitLimit { get; init; } = 4;
    public int RecommendationJobConcurrencyLimit { get; init; } = 1;
    public int RecommendationJobQueueLimit { get; init; }

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
    public TimeSpan CommentWindow => TimeSpan.FromSeconds(CommentWindowSeconds);

    public void Validate()
    {
        if (WindowSeconds is < 1 or > 3600)
            throw new InvalidOperationException("RateLimit:WindowSeconds must be between 1 and 3600.");
        if (CommentWindowSeconds is < 1 or > 3600)
            throw new InvalidOperationException("RateLimit:CommentWindowSeconds must be between 1 and 3600.");

        ValidatePositive(nameof(AuthPermitLimit), AuthPermitLimit);
        ValidatePositive(nameof(FeedPermitLimit), FeedPermitLimit);
        ValidatePositive(nameof(SearchPermitLimit), SearchPermitLimit);
        ValidatePositive(nameof(PlaybackPermitLimit), PlaybackPermitLimit);
        ValidatePositive(nameof(UploadPermitLimit), UploadPermitLimit);
        ValidatePositive(nameof(UploadPreflightPermitLimit), UploadPreflightPermitLimit);
        ValidatePositive(nameof(UploadChunkPermitLimit), UploadChunkPermitLimit);
        ValidatePositive(nameof(UploadStatusPermitLimit), UploadStatusPermitLimit);
        ValidatePositive(nameof(PaymentInitiatePermitLimit), PaymentInitiatePermitLimit);
        ValidatePositive(nameof(PaymentWebhookPermitLimit), PaymentWebhookPermitLimit);
        ValidatePositive(nameof(AdminPermitLimit), AdminPermitLimit);
        ValidatePositive(nameof(ModerationPermitLimit), ModerationPermitLimit);
        ValidatePositive(nameof(CommentPermitLimit), CommentPermitLimit);
        ValidatePositive(nameof(CfSeederPermitLimit), CfSeederPermitLimit);
        ValidatePositive(nameof(CfSeederUploadPermitLimit), CfSeederUploadPermitLimit);
        ValidatePositive(nameof(RecommendationJobPermitLimit), RecommendationJobPermitLimit);
        ValidatePositive(nameof(RecommendationJobConcurrencyLimit), RecommendationJobConcurrencyLimit);

        if (RecommendationJobQueueLimit < 0)
            throw new InvalidOperationException("RateLimit:RecommendationJobQueueLimit must be zero or greater.");
    }

    private static void ValidatePositive(string name, int value)
    {
        if (value < 1)
            throw new InvalidOperationException($"RateLimit:{name} must be at least 1.");
    }
}

internal static class RateLimitPolicies
{
    public static RateLimitPartition<string> FixedWindow(HttpContext context, int permitLimit, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });

    public static RateLimitPartition<string> SlidingWindow(HttpContext context, int permitLimit, TimeSpan window) =>
        RateLimitPartition.GetSlidingWindowLimiter(CommenterKey(context), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            SegmentsPerWindow = 10,
            QueueLimit = 0,
            AutoReplenishment = true
        });

    public static RateLimitPartition<string> GlobalRecommendationJobs(
        int permitLimit,
        TimeSpan window,
        int concurrencyLimit,
        int queueLimit) =>
        RateLimitPartition.Get("recommendation-jobs", _ => RateLimiter.CreateChained(
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true
            }),
            new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = concurrencyLimit,
                QueueLimit = queueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            })));

    private static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string CommenterKey(HttpContext context)
    {
        var userId = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return string.IsNullOrWhiteSpace(userId) ? $"ip:{ClientKey(context)}" : $"user:{userId}";
    }
}
