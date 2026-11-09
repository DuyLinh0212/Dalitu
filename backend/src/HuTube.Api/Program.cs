using System.Text;
using System.Net;
using System.Threading.RateLimiting;
using HuTube.Api.Middleware;
using HuTube.Api.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Hybrid;
using HuTube.Api.Services;
using HuTube.Application.Account;
using HuTube.Application.Auth;
using HuTube.Application.Channels;
using HuTube.Application.Notifications;
using HuTube.Application.Plans;
using HuTube.Application.Playlists;
using HuTube.Application.Storage;
using HuTube.Application.Videos;
using HuTube.Infrastructure.Account;
using HuTube.Application.CfSeeding;
using HuTube.Infrastructure.CfSeeding;
using HuTube.Infrastructure.Authentication;
using HuTube.Infrastructure.Caching;
using HuTube.Infrastructure.Persistence;
using HuTube.Infrastructure.Plans;
using HuTube.Infrastructure.Playlists;
using HuTube.Infrastructure.Storage;
using HuTube.Infrastructure.Taxonomy;
using HuTube.Infrastructure.Users;
using HuTube.Infrastructure.Videos;
using HuTube.Infrastructure.Operations;
using HuTube.Infrastructure.Notifications;
using HuTube.Infrastructure.Payments;
using HuTube.Infrastructure.Policies;
using HuTube.Application.Payments;
using HuTube.Application.Recommendations;
using HuTube.Infrastructure.Recommendations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var rawConnection = builder.Configuration.GetConnectionString("Database") ?? throw new InvalidOperationException("Set ConnectionStrings__Database using your environment or local secret file.");
var connection = DatabaseConnectionString.Normalize(rawConnection);
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new();
var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new();
var googleOptions = builder.Configuration.GetSection("Google").Get<GoogleOptions>() ?? new();
var emailOptions = builder.Configuration.GetSection("Email").Get<EmailOptions>() ?? new();
var storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new();
var r2Options = builder.Configuration.GetSection("Storage:R2").Get<R2Options>() ?? new();
var featureOptions = builder.Configuration.GetSection("Features").Get<FeatureOptions>() ?? new();
var videoProcessingOptions = builder.Configuration.GetSection("VideoProcessing").Get<VideoProcessingOptions>() ?? new();
var sepayOptions = builder.Configuration.GetSection("SePay").Get<SepayOptions>() ?? new();
var recommendationOptions = builder.Configuration.GetSection("Recommendation").Get<RecommendationOptions>() ?? new();
var rateLimitOptions = builder.Configuration.GetSection("RateLimit").Get<RateLimitOptions>() ?? new();
var geoIpOptions = builder.Configuration.GetSection("GeoIp").Get<GeoIpOptions>() ?? new();
rateLimitOptions.Validate();
// Accept the model service's environment names when both services share one
// deployment secret. Explicit Recommendation configuration takes precedence.
if (string.IsNullOrWhiteSpace(recommendationOptions.AdminToken))
    recommendationOptions.AdminToken = builder.Configuration["RECOMMENDER_ADMIN_TOKEN"] ?? "";
if (string.IsNullOrWhiteSpace(recommendationOptions.ServiceToken))
    recommendationOptions.ServiceToken = builder.Configuration["RECOMMENDER_SERVICE_TOKEN"] ?? "";
if (builder.Environment.IsDevelopment())
    R2OptionsLoader.LoadDevelopmentFile(r2Options, builder.Environment.ContentRootPath, builder.Configuration["Storage:R2:CredentialsFile"]);
if (jwt.SigningKey.Length < 32) throw new InvalidOperationException("Jwt__SigningKey must contain at least 32 random characters.");
if (authOptions.AccessTokenMinutes is < 1 or > 60 || authOptions.RefreshTokenDays is < 1 or > 90
    || authOptions.SessionInactivityDays is < 1 || authOptions.SessionInactivityDays > authOptions.RefreshTokenDays)
    throw new InvalidOperationException("Auth token lifetime configuration is outside its supported range.");
var requiredAuthOrigins = new[] { authOptions.WebBaseUrl, authOptions.AdminBaseUrl };
var additionalAuthOrigins = (authOptions.AllowedOrigins ?? []).Select(url => url.TrimEnd('/'))
    .Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
foreach (var url in requiredAuthOrigins)
    if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.AbsolutePath != "/" || parsed.Query.Length > 0 || parsed.Fragment.Length > 0
        || parsed.UserInfo.Length > 0 || (builder.Environment.IsDevelopment()
            ? parsed.Scheme is not ("http" or "https")
            : parsed.Scheme != "https" && !(parsed.IsLoopback && parsed.Scheme == "http" && additionalAuthOrigins.Contains(url.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))))
        throw new InvalidOperationException("Auth client URLs must be valid origins; non-HTTPS local origins must be explicitly allowlisted.");
foreach (var url in additionalAuthOrigins)
    if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.AbsolutePath != "/" || parsed.Query.Length > 0 || parsed.Fragment.Length > 0
        || parsed.UserInfo.Length > 0 || parsed.Scheme is not ("http" or "https")
        || (!builder.Environment.IsDevelopment() && parsed.Scheme == "http" && !parsed.IsLoopback))
        throw new InvalidOperationException("Auth client URLs must be valid origins; non-HTTPS local origins must be explicitly allowlisted.");
if (emailOptions.Mode is not ("Pickup" or "Smtp" or "GmailApi") || (!builder.Environment.IsDevelopment() && emailOptions.Mode == "Pickup"))
    throw new InvalidOperationException("Email pickup is Development-only. Configure SMTP or Gmail API for Staging/Production.");
if (emailOptions.Mode == "Smtp" && string.IsNullOrWhiteSpace(emailOptions.Host)) throw new InvalidOperationException("Email__Host is required for SMTP.");
if (emailOptions.Mode == "GmailApi" && (string.IsNullOrWhiteSpace(emailOptions.From)
    || string.IsNullOrWhiteSpace(emailOptions.Gmail.ClientId)
    || string.IsNullOrWhiteSpace(emailOptions.Gmail.ClientSecret)
    || string.IsNullOrWhiteSpace(emailOptions.Gmail.RefreshToken)))
    throw new InvalidOperationException("Email__From and Email__Gmail__ClientId/ClientSecret/RefreshToken are required for Gmail API.");

builder.Services.AddSingleton(jwt); builder.Services.AddSingleton(authOptions); builder.Services.AddSingleton(googleOptions); builder.Services.AddSingleton(emailOptions); builder.Services.AddSingleton(storageOptions); builder.Services.AddSingleton(r2Options); builder.Services.AddSingleton(featureOptions); builder.Services.AddSingleton(videoProcessingOptions); builder.Services.AddSingleton(sepayOptions); builder.Services.AddSingleton(recommendationOptions); builder.Services.AddSingleton(rateLimitOptions); builder.Services.AddSingleton(geoIpOptions);
builder.Services.AddSingleton<SepaySignatureVerifier>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("redis-rest", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddSingleton<UpstashRedisClient>();
builder.Services.AddHybridCache(options => {
    options.DefaultEntryOptions = new() {
        Expiration = TimeSpan.FromSeconds(30),
        Flags = HybridCacheEntryFlags.DisableLocalCache
    };
});
if (UpstashRedisClient.IsConfigured(builder.Configuration))
{
    var redisNamespace = $"dalitu:{builder.Environment.EnvironmentName.ToLowerInvariant()}";
    builder.Services.AddSingleton<IDistributedCache>(services => new UpstashDistributedCache(
        services.GetRequiredService<UpstashRedisClient>(), redisNamespace + ":cache:", TimeProvider.System));
    builder.Services.AddSingleton<RedisDataProtectionRepository>(services => new(
        services.GetRequiredService<UpstashRedisClient>(), redisNamespace + ":data-protection:keys"));
    builder.Services.AddSingleton<DataProtectionKeyEncryptor>();
    builder.Services.AddDataProtection().SetApplicationName(redisNamespace);
    builder.Services.AddOptions<KeyManagementOptions>()
        .Configure<RedisDataProtectionRepository, DataProtectionKeyEncryptor>((options, repository, encryptor) => {
            options.XmlRepository = repository;
            options.XmlEncryptor = encryptor;
        });
}
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 256L * 1024 * 1024 * 1024);
builder.Services.AddDbContext<HuTubeDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuthStore, AuthStore>(); builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<ISessionPlayerPreferencesStore, RedisSessionPlayerPreferencesStore>();
builder.Services.AddHostedService<AuthSessionCleanupWorker>();
builder.Services.AddScoped<HuTube.Application.Channels.IChannelStore, ChannelStore>();
builder.Services.AddScoped<HuTube.Application.Channels.ChannelService>();
builder.Services.AddScoped<HuTube.Application.Rbac.IRbacStore, RbacStore>();
builder.Services.AddScoped<HuTube.Application.Rbac.RbacService>();
builder.Services.AddSingleton<IPasswordService, PasswordService>(); builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IGoogleTokenVerifier, GoogleTokenVerifier>();
var hasCloudinaryCredentials = !string.IsNullOrWhiteSpace(storageOptions.CloudName)
    && !string.IsNullOrWhiteSpace(storageOptions.ApiKey)
    && !string.IsNullOrWhiteSpace(storageOptions.ApiSecret);
var hasR2Credentials = !string.IsNullOrWhiteSpace(r2Options.AccountId)
    && !string.IsNullOrWhiteSpace(r2Options.AccessKeyId)
    && !string.IsNullOrWhiteSpace(r2Options.SecretAccessKey)
    && !string.IsNullOrWhiteSpace(r2Options.BucketName);
if (!hasCloudinaryCredentials && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Cloudinary storage is required outside Development. Configure Storage__CloudName, Storage__ApiKey and Storage__ApiSecret for images.");
if (!hasR2Credentials && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Cloudflare R2 storage is required outside Development. Configure Storage__R2__AccountId, AccessKeyId, SecretAccessKey and BucketName for videos.");
if (hasCloudinaryCredentials) builder.Services.AddSingleton<CloudinaryStorageService>();
else builder.Services.AddSingleton<LocalStorageService>();
if (hasR2Credentials) builder.Services.AddSingleton<R2ObjectStorageService>();
else builder.Services.AddSingleton<LocalStorageService>();
builder.Services.AddSingleton<IObjectStorage>(services => new DualObjectStorageService(
    hasCloudinaryCredentials ? services.GetRequiredService<CloudinaryStorageService>() : services.GetRequiredService<LocalStorageService>(),
    hasR2Credentials ? services.GetRequiredService<R2ObjectStorageService>() : services.GetRequiredService<LocalStorageService>()));
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddSingleton(builder.Configuration.GetSection("FeedbackSurvey").Get<FeedbackSurveyOptions>() ?? new());
builder.Services.AddSingleton<FeedbackSurveySecurity>();
builder.Services.AddScoped<IFeedbackSurveyService, FeedbackSurveyService>();
builder.Services.AddScoped<IContentService, ContentService>();
builder.Services.AddScoped<ICfSeederService, CfSeederService>();
builder.Services.AddSingleton<VideoRenditionProcessingQueue>();
builder.Services.AddScoped<VideoRenditionProcessor>();
builder.Services.AddHostedService<VideoRenditionProcessingWorker>();
builder.Services.AddHostedService<VideoMediaRetentionCleanupService>();
builder.Services.AddSingleton(_ => new CfSeedChunkUploadStore(builder.Configuration["CfSeedUpload:Directory"]));
builder.Services.AddSingleton<VideoChunkUploadStore>();
builder.Services.AddSingleton<VideoUploadCompletionWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<VideoUploadCompletionWorker>());
builder.Services.AddScoped<IPlanService, PlanService>();
builder.Services.AddHostedService<PlanExpirationWorker>();
builder.Services.AddHostedService<ChannelSuspensionExpirationWorker>();
builder.Services.AddScoped<AdminSubscriptionService>();
builder.Services.AddScoped<AdminPaymentService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddHostedService<PaymentExpirationWorker>();
 builder.Services.AddScoped<IPlaylistService, PlaylistService>();
builder.Services.AddScoped<PolicyService>();
builder.Services.AddScoped<ModerationService>();
builder.Services.AddScoped<StrikePolicySettingsService>();
builder.Services.AddScoped<StrikeService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<AdminContentService>();
builder.Services.AddScoped<AdminOperationsService>();
builder.Services.AddScoped<AdminDatabaseBackupService>();
builder.Services.AddHostedService<DatabaseBackupWorker>();
builder.Services.AddSingleton(new ApiRequestTelemetry());
builder.Services.AddScoped<AppealService>();
builder.Services.AddScoped<TaxonomyService>();
builder.Services.AddScoped<AdminUserService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
var rabbitMq = builder.Configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new();
if (rabbitMq.Enabled && string.IsNullOrWhiteSpace(rabbitMq.Password))
    throw new InvalidOperationException("RabbitMq__Password is required when messaging is enabled.");
builder.Services.AddSingleton(rabbitMq);
builder.Services.AddSingleton<RabbitMqTransport>();
builder.Services.AddSingleton<IModerationEventPublisher>(provider => provider.GetRequiredService<RabbitMqTransport>());
builder.Services.AddScoped<ModerationOutboxPublisher>();
builder.Services.AddScoped<NotificationFanout>();
builder.Services.AddScoped<NotificationDeliveryProcessor>();
builder.Services.AddScoped<PushDeviceService>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Fcm").Get<FcmOptions>() ?? new());
builder.Services.AddSingleton<FcmCredentialProvider>();
builder.Services.AddHttpClient<IPushNotificationSender, FcmPushNotificationSender>(http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<ModerationOutboxWorker>();
builder.Services.AddHostedService<ModerationNotificationWorker>();
builder.Services.AddHostedService<NotificationDeliveryWorker>();
builder.Services.AddSingleton<IVideoTranscoder, FfmpegVideoTranscoder>();
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, NotificationUserIdProvider>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<NotificationConnections>();
builder.Services.AddHostedService<NotificationSessionMonitor>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<IClientLocationResolver, IpApiClientLocationResolver>(client =>
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Dalitu/1.0 (IP geolocation for login security)"));
builder.Services.AddHttpClient<IRecommendationClient, RecommendationClient>();
// Model snapshots may use a separate bucket/endpoint from video storage.
// Without an override, retain the existing R2 configuration.
var recommendationStorage = builder.Configuration.GetSection("Recommendation:Storage").Get<R2Options>() ?? r2Options;
builder.Services.AddScoped<IRecommendationSnapshotStore>(_ => new RecommendationSnapshotStore(recommendationStorage));
builder.Services.AddScoped<RecommendationAdminService>();
builder.Services.AddHostedService<RecommendationJobWorker>();
builder.Services.AddSingleton<IAuthEmailSender, AuthEmailSender>();
builder.Services.AddScoped<ReadModelCacheFilter>();
builder.Services.AddControllers(options => options.Filters.AddService<ReadModelCacheFilter>())
    .AddJsonOptions(options => options.JsonSerializerOptions.TypeInfoResolver = WebImageDelivery.CreateResolver())
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context => {
    context.HttpContext.Items[ApiErrors.ErrorCodeItemKey] = "VALIDATION_ERROR";
    var problem = ApiErrors.Create(context.HttpContext, 400, "VALIDATION_ERROR", "Vui lòng kiểm tra các trường dữ liệu.");
    problem.Extensions["errors"] = context.ModelState.Where(x => x.Value?.Errors.Count > 0).ToDictionary(x => x.Key, x => x.Value!.Errors.Select(_ => "Giá trị không hợp lệ.").ToArray());
    var result = new BadRequestObjectResult(problem); result.ContentTypes.Add("application/problem+json"); return result;
});
builder.Services.AddOpenApi();
var corsOrigins = requiredAuthOrigins.Concat(additionalAuthOrigins).Select(url => url.TrimEnd('/'))
    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(corsOrigins)
    .WithMethods("GET", "POST", "PATCH", "PUT", "DELETE", "OPTIONS")
    .WithHeaders("Accept", "Content-Type", "Authorization", "Idempotency-Key", "Prefer", "Cache-Control", "X-Upload-Chunk-Index", "X-Requested-With", "X-SignalR-User-Agent", "X-HuTube-Client", "X-HuTube-App")
    .WithExposedHeaders("Retry-After", "X-Request-ID", "X-Data-Cache")
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
    .AllowCredentials()));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => {
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new() {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.Zero, ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
    };
    options.Events = new() {
        OnMessageReceived = context => {
            var token = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/notifications")) context.Token = token;
            return Task.CompletedTask;
        },
        OnTokenValidated = async context => {
            try
            {
                if (!Guid.TryParse(context.Principal?.FindFirst("sub")?.Value, out var userId)
                    || !Guid.TryParse(context.Principal?.FindFirst("sid")?.Value, out var sessionId)
                    || !Guid.TryParse(context.Principal?.FindFirst("jti")?.Value, out var jti)
                    || !await context.HttpContext.RequestServices.GetRequiredService<AuthService>().ValidateSessionAsync(
                        userId, sessionId, jti, context.HttpContext.RequestAborted, context.HttpContext.Connection.RemoteIpAddress?.ToString()))
                    context.Fail("SESSION_EXPIRED");
            }
            catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                // The client disconnected while session validation was in flight.
                // Do not let an expected request cancellation become a JWT error log.
                context.NoResult();
            }
        },
        OnChallenge = async context => { context.HandleResponse(); await ApiErrors.WriteAsync(context.HttpContext, 401, "SESSION_EXPIRED", "Phiên đã hết hạn. Vui lòng đăng nhập lại."); },
        OnForbidden = context => ApiErrors.WriteAsync(context.HttpContext, 403, "PERMISSION_DENIED", "Bạn không có quyền thực hiện thao tác này.")
    };
});
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options => {
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Render can put more than one trusted hop in X-Forwarded-For (for example
    // the edge proxy and the service ingress). Process the complete chain and
    // stop at the first untrusted address instead of persisting a proxy IP.
    options.ForwardLimit = null;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    // Production ingress addresses/networks must be explicitly trusted; never accept arbitrary X-Forwarded-For values.
    foreach (var value in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        if (IPAddress.TryParse(value, out var proxy)) options.KnownProxies.Add(proxy);
    foreach (var value in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix)
            || !int.TryParse(parts[1], out var prefixLength)
            || prefixLength < 0 || prefixLength > prefix.GetAddressBytes().Length * 8)
            throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks contains an invalid CIDR value: '{value}'.");
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
    }
});
builder.Services.AddRateLimiter(options => {
    options.AddPolicy("auth", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.AuthPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("feed", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.FeedPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("search", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.SearchPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("playback", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.PlaybackPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("upload", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.UploadPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("upload-preflight", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.UploadPreflightPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("upload-chunk", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.UploadChunkPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("upload-status", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.UploadStatusPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("payment-initiate", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.PaymentInitiatePermitLimit, rateLimitOptions.Window));
    options.AddPolicy("payment-webhook", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.PaymentWebhookPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("feedback-survey-webhook", context => RateLimitPolicies.FixedWindow(context, 120, rateLimitOptions.Window));
    options.AddPolicy("admin", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.AdminPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("moderation", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.ModerationPermitLimit, rateLimitOptions.Window));
    options.AddPolicy(RateLimitOptions.CommentCreationPolicy, context => RateLimitPolicies.SlidingWindow(
        context, rateLimitOptions.CommentPermitLimit, rateLimitOptions.CommentWindow));
    options.AddPolicy("cf-seeder", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.CfSeederPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("cf-seeder-upload", context => RateLimitPolicies.FixedWindow(context, rateLimitOptions.CfSeederUploadPermitLimit, rateLimitOptions.Window));
    options.AddPolicy("recommendation-job", _ => RateLimitPolicies.GlobalRecommendationJobs(
        rateLimitOptions.RecommendationJobPermitLimit,
        rateLimitOptions.Window,
        rateLimitOptions.RecommendationJobConcurrencyLimit,
        rateLimitOptions.RecommendationJobQueueLimit));
    options.OnRejected = async (context, _) => {
        var isCommentLimit = context.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RateLimitOptions.CommentCreationPolicy;
        var retryAfterSeconds = isCommentLimit ? rateLimitOptions.CommentWindowSeconds : rateLimitOptions.WindowSeconds;
        context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        await ApiErrors.WriteAsync(context.HttpContext, 429,
            isCommentLimit ? "COMMENT_RATE_LIMIT_EXCEEDED" : "RATE_LIMIT_EXCEEDED",
            isCommentLimit
                ? $"Bạn đang bình luận quá nhanh. Vui lòng đợi {retryAfterSeconds} giây rồi thử lại."
                : "Yêu cầu quá nhanh. Vui lòng thử lại sau một phút.");
    };
});

var app = builder.Build();
if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<HuTubeDbContext>().Database.MigrateAsync();
    return;
}
app.UseForwardedHeaders();
app.UseMiddleware<RecommendationProxyMiddleware>();
app.Use(async (context, next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Request-ID"] = context.TraceIdentifier;
    await next(context);
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseRouting();
app.UseCors();
var uploadsPath = Path.Combine(builder.Environment.ContentRootPath, "uploads");
Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
});
app.UseMiddleware<ApiRequestTelemetryMiddleware>();
app.UseMiddleware<ExceptionMiddleware>();
app.UseAuthentication(); app.UseRateLimiter(); app.UseMiddleware<AdminApiAuditMiddleware>(); app.UseAuthorization();
app.UseStatusCodePages(context => ApiErrors.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode, "HTTP_ERROR", "Yêu cầu không được xử lý."));
if (!app.Environment.IsProduction()) {
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Dalitu API v1"));
}
app.MapGet("/health", async (HuTubeDbContext db, CancellationToken ct) => {
    try { return await db.Database.CanConnectAsync(ct) && await db.Users.OrderBy(x => x.UserId).Take(1).CountAsync(ct) >= 0
        ? Results.Ok(new { status = "healthy" }) : Results.Json(new { status = "unhealthy" }, statusCode: 503); }
    catch { return Results.Json(new { status = "unhealthy" }, statusCode: 503); }
}).AllowAnonymous();
app.MapGet("/api/v1/system/info", () => new { name = "Dalitu", apiVersion = "v1", environment = app.Environment.EnvironmentName,
    serverTime = DateTimeOffset.UtcNow, commitSha = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") ?? Environment.GetEnvironmentVariable("HUTUBE_COMMIT_SHA") ?? "local" }).AllowAnonymous();
app.MapGet("/api/v1/system/config", () => new { googleClientId = googleOptions.ClientId }).AllowAnonymous();
app.MapControllers();
app.MapHub<NotificationHub>("/hubs/notifications", options => options.CloseOnAuthenticationExpiration = true);
app.Run();
public partial class Program { }
