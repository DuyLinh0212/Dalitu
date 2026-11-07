using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HuTube.Application.Storage;
using HuTube.Application.Operations;
using HuTube.Domain.Rbac;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Persistence;
using HuTube.Infrastructure.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HuTube.Infrastructure.Operations;

public sealed class AdminOperationsService(
    HuTubeDbContext db,
    IConfiguration configuration,
    IHostEnvironment environment,
    ApiRequestTelemetry telemetry,
    NotificationConnections notificationConnections,
    IObjectStorage objectStorage,
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor, ILogger<AdminOperationsService> logger,
    HybridCache? readCache = null)
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProviderMetricsTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SignalRProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan R2UsageCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UpstashUsageCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NeonUsageCacheDuration = TimeSpan.FromMinutes(15);
    private const string CloudflareGraphQlEndpoint = "https://api.cloudflare.com/client/v4/graphql";

    public sealed record CounterSnapshot(long Signups, long Locked, long Abuse, long Reviews,
        long FailedPayments, long Sessions, long PendingPayments, long ExpiredPayments,
        long ProcessingVideos, long FailedVideos, long ProcessingRenditions, long FailedRenditions);

    private Task<CounterSnapshot> ReadCountersAsync(DateTimeOffset now, CancellationToken ct) => readCache is null
        ? QueryCountersAsync(now, ct)
        : readCache.GetOrCreateAsync($"operations:counters:v1:{now.ToUnixTimeSeconds() / 15}",
            token => new ValueTask<CounterSnapshot>(QueryCountersAsync(now, token)),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(15),
                Flags = HybridCacheEntryFlags.DisableLocalCache },
            cancellationToken: ct).AsTask();

    private Task<CounterSnapshot> QueryCountersAsync(DateTimeOffset now, CancellationToken ct) =>
        db.Database.SqlQuery<CounterSnapshot>($"""
            SELECT
                (SELECT count(*) FROM public.users WHERE deleted_at IS NULL AND created_at >= {now.AddHours(-1)} AND created_at < {now}) AS "Signups",
                (SELECT count(*) FROM public.users WHERE deleted_at IS NULL AND locked_until > {now}) AS "Locked",
                (SELECT count(*) FROM public.reports WHERE abuse_flag AND status = 'pending') AS "Abuse",
                (SELECT count(*) FROM public.moderation_cases WHERE case_type = 'upload_review' AND status = 'pending') AS "Reviews",
                (SELECT count(*) FROM public.payments WHERE status IN ('failed','cancelled') AND updated_at >= {now.AddDays(-1)} AND updated_at < {now}) AS "FailedPayments",
                (SELECT count(*) FROM public.refresh_tokens WHERE revoked_at IS NULL AND expires_at > {now}) AS "Sessions",
                (SELECT count(*) FROM public.payments WHERE status = 'pending') AS "PendingPayments",
                (SELECT count(*) FROM public.payments WHERE status = 'pending' AND expires_at <= {now}) AS "ExpiredPayments",
                (SELECT count(*) FROM public.videos WHERE status = 'processing') AS "ProcessingVideos",
                (SELECT count(*) FROM public.videos WHERE status = 'failed' AND updated_at >= {now.AddDays(-1)} AND updated_at < {now}) AS "FailedVideos",
                (SELECT count(*) FROM public.video_renditions WHERE status = 'processing') AS "ProcessingRenditions",
                (SELECT count(*) FROM public.video_renditions WHERE status = 'failed' AND updated_at >= {now.AddDays(-1)} AND updated_at < {now}) AS "FailedRenditions"
            """).SingleAsync(ct);

    private async Task<T> GetL2Async<T>(string key, TimeSpan expiration,
        Func<CancellationToken, Task<T>> factory, CancellationToken ct) where T : class
    {
        if (readCache is null) return await factory(ct);
        try
        {
            return await readCache.GetOrCreateAsync(key,
                token => new ValueTask<T>(factory(token)),
                new HybridCacheEntryOptions {
                    Expiration = expiration,
                    Flags = HybridCacheEntryFlags.DisableLocalCache
                },
                cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Redis cache unavailable for admin provider usage key {CacheKey}; querying provider.", key);
            return await factory(ct);
        }
    }

    public async Task<AdminSystemOverviewResponse> GetSystemOverviewAsync(CancellationToken ct)
    {
        var overviewTimer = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);
        var databaseTimer = Stopwatch.StartNew();
        var signupThreshold = Math.Max(1, configuration.GetValue("SystemAlerts:SignupBurstPerHour", 20));
        var reviewThreshold = Math.Max(1, configuration.GetValue("SystemAlerts:PendingReviewThreshold", 50));
        var paymentFailureThreshold = Math.Max(1, configuration.GetValue("SystemAlerts:FailedPaymentsPerDay", 10));
        var apiErrorThreshold = Math.Max(1, configuration.GetValue("SystemAlerts:ApiErrorsPer15Minutes", 5));
        var memoryWarningThreshold = Math.Clamp(configuration.GetValue("SystemAlerts:MemoryWarningPercent", 75), 1, 99);

        var counts = await ReadCountersAsync(now, ct);
        var (signupsLastHour, lockedAccounts, abuseReports, pendingReviews, failedPayments, activeSessions,
            pendingPaymentCount, expiredPendingPayments, processingVideos, failedVideos, processingRenditions, failedRenditions) = counts;
        databaseTimer.Stop();

        var apiTraffic = telemetry.Snapshot(now);
        var apiErrors = apiTraffic.ServerErrorsLast15Minutes;

        var (memoryPercent, workingSetBytes, availableBytes) = ReadMemoryUsage();
        var ec2Memory = ReadEc2MemoryUsage(now);
        var signals = new List<AdminSystemSignal>();
        if (memoryPercent is int currentMemoryPercent)
        {
            var memoryTriggered = currentMemoryPercent >= memoryWarningThreshold;
            signals.Add(new AdminSystemSignal("memory", "runtime",
                memoryTriggered ? (currentMemoryPercent >= 90 ? "critical" : "warning") : "normal",
                "Bộ nhớ tiến trình API", $"Tiến trình API đang sử dụng {currentMemoryPercent}% giới hạn bộ nhớ khả dụng.",
                currentMemoryPercent, "%", memoryWarningThreshold, null, memoryTriggered));
        }
        signals.AddRange(
        [
            new("signup-burst", "security", signupsLastHour >= signupThreshold ? "warning" : "normal",
                "Tốc độ tạo tài khoản", $"Có {signupsLastHour:N0} tài khoản mới trong 60 phút gần nhất.",
                signupsLastHour, "tài khoản/giờ", signupThreshold, "/users", signupsLastHour >= signupThreshold),
            new("locked-accounts", "security", lockedAccounts > 0 ? "warning" : "normal",
                "Tài khoản đang bị khóa đăng nhập", $"Có {lockedAccounts:N0} tài khoản đang trong thời gian khóa đăng nhập.",
                lockedAccounts, "tài khoản", 1, "/users", lockedAccounts > 0),
            new("abuse-reports", "moderation", abuseReports > 0 ? "warning" : "normal",
                "Báo cáo bị đánh dấu lạm dụng", $"Có {abuseReports:N0} báo cáo spam/lạm dụng đang chờ xử lý.",
                abuseReports, "báo cáo", 1, "/moderation/reports", abuseReports > 0),
            new("moderation-backlog", "moderation", pendingReviews >= reviewThreshold ? "warning" : "normal",
                "Hàng đợi kiểm duyệt video", $"Có {pendingReviews:N0} video đang chờ duyệt.",
                pendingReviews, "video", reviewThreshold, "/moderation/videos", pendingReviews >= reviewThreshold),
            new("payment-failures", "payments", failedPayments >= paymentFailureThreshold ? "warning" : "normal",
                "Giao dịch lỗi trong 24 giờ", $"Có {failedPayments:N0} giao dịch thất bại hoặc bị hủy trong 24 giờ gần nhất.",
                failedPayments, "giao dịch/ngày", paymentFailureThreshold, "/payments", failedPayments >= paymentFailureThreshold),
            new("api-errors", "runtime", apiErrors >= apiErrorThreshold ? "warning" : "normal",
                "Lỗi máy chủ API gần đây", $"Có {apiErrors:N0} request API trả về lỗi máy chủ trong 15 phút gần nhất.",
                apiErrors, "lỗi/15 phút", apiErrorThreshold, "/system/logs", apiErrors >= apiErrorThreshold)
        ]);

        var triggered = signals.Where(signal => signal.IsTriggered).ToList();
        var rateLimit = configuration.GetValue("RateLimit:AuthPermitLimit", 60);
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var settings = new List<AdminSystemRuntimeSetting>
        {
            new("Môi trường ứng dụng", environment.EnvironmentName, "ASPNETCORE_ENVIRONMENT"),
            new("API", "v1", "Định tuyến /api/v1"),
            new("Phiên bản backend", version, "Assembly đang chạy"),
            new("Giới hạn xác thực", $"{rateLimit} yêu cầu/phút/IP", "RateLimit:AuthPermitLimit"),
            new("Ngưỡng cảnh báo đăng ký", $"{signupThreshold} tài khoản/giờ", "SystemAlerts:SignupBurstPerHour"),
            new("Ngưỡng hàng đợi kiểm duyệt", $"{reviewThreshold} video", "SystemAlerts:PendingReviewThreshold"),
            new("Ngưỡng giao dịch lỗi", $"{paymentFailureThreshold} giao dịch/24 giờ", "SystemAlerts:FailedPaymentsPerDay"),
            new("Ngưỡng lỗi API", $"{apiErrorThreshold} lỗi/15 phút", "SystemAlerts:ApiErrorsPer15Minutes"),
            new("Cảnh báo bộ nhớ", $"{memoryWarningThreshold}%", "SystemAlerts:MemoryWarningPercent"),
            new("Bộ nhớ tiến trình", $"{FormatBytes(workingSetBytes)} / {FormatBytes(availableBytes)}", "Process.WorkingSet64 / GC memory limit")
        };

        var liveMetrics = new AdminSystemLiveMetrics(
            activeSessions,
            pendingPaymentCount,
            expiredPendingPayments,
            failedPayments,
            processingVideos,
            failedVideos,
            processingRenditions,
            failedRenditions,
            pendingReviews,
            notificationConnections.Active.Count);
        overviewTimer.Stop();
        var r2StorageUsageTask = GetR2StorageUsageAsync(ct);
        var redisUsageTask = GetRedisUsageAsync(ct);
        var servicesTask = GetServiceStatusesAsync(databaseTimer.Elapsed.TotalMilliseconds,
            overviewTimer.Elapsed.TotalMilliseconds, notificationConnections.Active.Count, ec2Memory, ct);
        var neonUsageTask = GetNeonUsageAsync(ct);
        var services = await servicesTask;
        var r2StorageUsage = await r2StorageUsageTask;
        var neonUsage = await neonUsageTask;
        var redisUsage = await redisUsageTask;
        await RecordSignalTransitionsAsync(signals, now, ct);
        var incidentHistory = await GetIncidentHistoryAsync(ct);
        var recentActivity = await GetRecentActivityAsync(ct);

        return new AdminSystemOverviewResponse(now, triggered.Count, triggered.Count(signal => signal.Severity == "critical"),
            signals, settings, apiTraffic, liveMetrics, services, r2StorageUsage, neonUsage, redisUsage,
            ec2Memory, recentActivity, incidentHistory);
    }

    private async Task<IReadOnlyList<AdminSystemServiceStatus>> GetServiceStatusesAsync(
        double databaseResponseTimeMs,
        double overviewResponseTimeMs,
        int activeSignalRConnections,
        AdminEc2MemoryUsage ec2Memory,
        CancellationToken ct)
    {
        var redisTask = CheckRedisAsync(ct);
        var vercelTask = CheckVercelAsync(ct);
        var r2Task = CheckR2Async(ct);
        var signalRTask = CheckSignalRAsync(activeSignalRConnections, ct);

        return
        [
            new("api", "healthy", "Endpoint giám sát vừa xử lý dữ liệu thành công.", overviewResponseTimeMs),
            new("database", "healthy", "Truy vấn database Neon/PostgreSQL thành công.", databaseResponseTimeMs),
            await redisTask,
            new("ec2", ec2Memory.Status == "available" ? "healthy" : ec2Memory.Status,
                ec2Memory.Detail, null),
            await vercelTask,
            await r2Task,
            await signalRTask
        ];
    }

    private async Task<AdminSystemServiceStatus> CheckSignalRAsync(int activeConnections, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var context = httpContextAccessor.HttpContext;
        var port = context?.Connection.LocalPort ?? 0;
        if (context is null || port is <= 0 or > 65535)
            return new("signalr", "unmonitored", "Không xác định được cổng nội bộ để kiểm tra SignalR.", null);

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers["Authorization"].ToString(), out var authorization)
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorization.Parameter))
            return new("signalr", "unmonitored", "Request hiện tại không có phiên Bearer để kiểm tra bắt tay SignalR.", null);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignalRProbeTimeout);
        try
        {
            var localBaseUrl = $"http://127.0.0.1:{port}";
            using var negotiateRequest = new HttpRequestMessage(HttpMethod.Post,
                $"{localBaseUrl}/hubs/notifications/negotiate?negotiateVersion=1");
            negotiateRequest.Headers.Authorization = authorization;
            using var negotiateResponse = await httpClientFactory.CreateClient().SendAsync(negotiateRequest,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!negotiateResponse.IsSuccessStatusCode)
                return new("signalr", "error",
                    $"SignalR negotiate trả HTTP {(int)negotiateResponse.StatusCode}.", timer.Elapsed.TotalMilliseconds);

            await using var responseStream = await negotiateResponse.Content.ReadAsStreamAsync(timeout.Token);
            using var negotiation = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeout.Token);
            if (!negotiation.RootElement.TryGetProperty("connectionToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tokenElement.GetString()))
                return new("signalr", "error", "SignalR negotiate không trả connection token hợp lệ.", timer.Elapsed.TotalMilliseconds);

            var connectionToken = Uri.EscapeDataString(tokenElement.GetString()!);
            var socketUri = new UriBuilder("ws", "127.0.0.1", port, "/hubs/notifications")
            {
                Query = $"id={connectionToken}"
            }.Uri;
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", authorization.ToString());
            await socket.ConnectAsync(socketUri, timeout.Token);

            var handshake = Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e");
            await socket.SendAsync(handshake.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);

            using var handshakeResponse = new MemoryStream();
            var receiveBuffer = new byte[512];
            ValueWebSocketReceiveResult receiveResult;
            do
            {
                receiveResult = await socket.ReceiveAsync(receiveBuffer.AsMemory(), timeout.Token);
                if (receiveResult.MessageType != WebSocketMessageType.Text)
                    return new("signalr", "error", "SignalR trả phản hồi handshake không phải văn bản.", timer.Elapsed.TotalMilliseconds);

                handshakeResponse.Write(receiveBuffer, 0, receiveResult.Count);
                if (handshakeResponse.Length > 4096)
                    return new("signalr", "error", "Phản hồi handshake SignalR vượt quá kích thước cho phép.", timer.Elapsed.TotalMilliseconds);
            }
            while (!receiveResult.EndOfMessage);

            var handshakeText = Encoding.UTF8.GetString(handshakeResponse.ToArray());
            if (!handshakeText.StartsWith("{}\u001e", StringComparison.Ordinal))
                return new("signalr", "error", "SignalR đã mở WebSocket nhưng từ chối giao thức JSON handshake.", timer.Elapsed.TotalMilliseconds);

            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "health probe", timeout.Token);
            return new("signalr", "healthy",
                $"SignalR negotiate và WebSocket handshake thành công; có {activeConnections:N0} kết nối realtime đang mở.",
                timer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new("signalr", "error", "Kiểm tra bắt tay SignalR quá thời gian 5 giây.", SignalRProbeTimeout.TotalMilliseconds);
        }
        catch (HttpRequestException)
        {
            return new("signalr", "error", "Không gọi được SignalR negotiate trên instance API.", timer.Elapsed.TotalMilliseconds);
        }
        catch (WebSocketException)
        {
            return new("signalr", "error", "Không mở hoặc hoàn tất được WebSocket handshake SignalR.", timer.Elapsed.TotalMilliseconds);
        }
        catch (JsonException)
        {
            return new("signalr", "error", "SignalR negotiate trả dữ liệu không hợp lệ.", timer.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<AdminSystemServiceStatus> CheckRedisAsync(CancellationToken ct)
    {
        var endpoint = configuration["UPSTASH_REDIS_REST_URL"] ?? configuration["SystemMonitoring:RedisRestUrl"];
        var token = configuration["UPSTASH_REDIS_REST_TOKEN"] ?? configuration["SystemMonitoring:RedisRestToken"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token))
            return new("redis", "unmonitored",
                "Backend chưa cấu hình URL/token kiểm tra Upstash Redis và hiện không dùng Redis.", null);
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/ping", UriKind.Absolute, out var pingUri)
            || pingUri.Scheme != Uri.UriSchemeHttps)
            return new("redis", "error", "URL kiểm tra Upstash Redis không hợp lệ; cần dùng HTTPS.", null);

        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HealthProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pingUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClientFactory.CreateClient().SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new("redis", "error", $"Upstash Redis trả HTTP {(int)response.StatusCode} khi ping.", timer.Elapsed.TotalMilliseconds);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var result = payload.RootElement.TryGetProperty("result", out var value) ? value.GetString() : null;
            return string.Equals(result, "PONG", StringComparison.OrdinalIgnoreCase)
                ? new("redis", "healthy", "Upstash Redis phản hồi PONG.", timer.Elapsed.TotalMilliseconds)
                : new("redis", "error", "Upstash Redis không trả kết quả PONG.", timer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new("redis", "error", "Ping Upstash Redis quá thời gian 3 giây.", HealthProbeTimeout.TotalMilliseconds);
        }
        catch (HttpRequestException)
        {
            return new("redis", "error", "Không kết nối được tới Upstash Redis.", timer.Elapsed.TotalMilliseconds);
        }
        catch (JsonException)
        {
            return new("redis", "error", "Upstash Redis trả dữ liệu ping không hợp lệ.", timer.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<AdminSystemServiceStatus> CheckVercelAsync(CancellationToken ct)
    {
        var sites = new[]
        {
            (Name: "admin", Url: configuration["Auth:AdminBaseUrl"]),
            (Name: "user", Url: configuration["Auth:WebBaseUrl"])
        }.Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase).ToArray();
        if (sites.Length == 0)
            return new("vercel", "unmonitored", "Chưa cấu hình URL frontend để kiểm tra Vercel.", null);

        var results = await Task.WhenAll(sites.Select(async site =>
            (site.Name, Result: await ProbeHttpAsync(site.Url!, ct))));
        var failed = results.Where(result => !result.Result.IsHealthy).ToArray();
        var detail = string.Join("; ", results.Select(result => result.Result.StatusCode == 0
            ? $"{result.Name}: không kết nối được"
            : $"{result.Name}: HTTP {result.Result.StatusCode}"));
        var status = failed.Length == 0 ? "healthy"
            : failed.Any(result => result.Result.StatusCode >= 500 || result.Result.StatusCode == 0) ? "error" : "warning";
        return new("vercel", status, detail, results.Average(result => result.Result.ResponseTimeMs));
    }

    private async Task<AdminSystemServiceStatus> CheckR2Async(CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HealthProbeTimeout);
        try
        {
            var result = await objectStorage.CheckHealthAsync(timeout.Token);
            return result switch
            {
                true => new("storage", "healthy", "Cloudflare R2 nhận được yêu cầu liệt kê tối đa 1 object.", timer.Elapsed.TotalMilliseconds),
                false => new("storage", "error", "Cloudflare R2 không phản hồi kiểm tra bucket.", timer.Elapsed.TotalMilliseconds),
                _ => new("storage", "unmonitored", "Storage hiện tại không cung cấp phép kiểm tra bucket Cloudflare R2.", null)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new("storage", "error", "Kiểm tra Cloudflare R2 quá thời gian 3 giây.", HealthProbeTimeout.TotalMilliseconds);
        }
        catch (Exception)
        {
            return new("storage", "error", "Cloudflare R2 không phản hồi kiểm tra bucket.", timer.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<AdminR2StorageUsage> GetR2StorageUsageAsync(CancellationToken ct)
    {
        var accountId = configuration["Storage:R2:AccountId"]?.Trim();
        var bucketName = configuration["Storage:R2:BucketName"]?.Trim();
        var token = (configuration["CLOUDFLARE_API_TOKEN"]
            ?? configuration["SystemMonitoring:CloudflareAnalyticsToken"])?.Trim();

        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(bucketName))
            return new("unmonitored", bucketName ?? "", null, null, null, null, null, null,
                "Chưa cấu hình Account ID và tên bucket R2.");
        if (string.IsNullOrWhiteSpace(token))
            return new("unmonitored", bucketName, null, null, null, null, null, null,
                "Chưa cấu hình CLOUDFLARE_API_TOKEN có quyền Account Analytics: Read.");

        var cacheKey = $"admin-r2-storage-usage:{accountId}:{bucketName}";
        return await GetL2Async(cacheKey, R2UsageCacheDuration,
            cacheCt => QueryR2StorageUsageAsync(accountId, bucketName, token, cacheCt), ct);
    }

    private async Task<AdminR2StorageUsage> QueryR2StorageUsageAsync(
        string accountId,
        string bucketName,
        string token,
        CancellationToken ct)
    {
        const string query = """
            query R2StorageUsage($accountTag: string!, $startDate: Time, $endDate: Time, $bucketName: string) {
              viewer {
                accounts(filter: { accountTag: $accountTag }) {
                  r2StorageAdaptiveGroups(
                    limit: 1
                    filter: {
                      datetime_geq: $startDate
                      datetime_leq: $endDate
                      bucketName: $bucketName
                    }
                    orderBy: [datetime_DESC]
                  ) {
                    max { objectCount uploadCount payloadSize metadataSize }
                    dimensions { datetime }
                  }
                }
              }
            }
            """;

        var now = DateTimeOffset.UtcNow;
        var requestPayload = JsonSerializer.Serialize(new
        {
            query,
            variables = new
            {
                accountTag = accountId,
                startDate = now.AddDays(-30).ToString("O", CultureInfo.InvariantCulture),
                endDate = now.ToString("O", CultureInfo.InvariantCulture),
                bucketName
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HealthProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CloudflareGraphQlEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(requestPayload, System.Text.Encoding.UTF8, "application/json");
            using var response = await httpClientFactory.CreateClient().SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new("error", bucketName, null, null, null, null, null, null,
                    response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        ? "Cloudflare từ chối token; cần quyền Account Analytics: Read cho đúng account."
                        : $"Cloudflare Analytics trả HTTP {(int)response.StatusCode} khi đọc dung lượng R2.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var root = payload.RootElement;
            if (root.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                return new("error", bucketName, null, null, null, null, null, null,
                    "Cloudflare Analytics không chấp nhận truy vấn; kiểm tra quyền Account Analytics: Read và cấu hình bucket.");

            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("viewer", out var viewer)
                || !viewer.TryGetProperty("accounts", out var accounts)
                || accounts.ValueKind != JsonValueKind.Array
                || accounts.GetArrayLength() == 0
                || !accounts[0].TryGetProperty("r2StorageAdaptiveGroups", out var groups)
                || groups.ValueKind != JsonValueKind.Array
                || groups.GetArrayLength() == 0)
                return new("unmonitored", bucketName, null, null, null, null, null, null,
                    "Cloudflare chưa có mẫu dung lượng cho bucket này trong 30 ngày gần nhất.");

            var group = groups[0];
            if (!group.TryGetProperty("max", out var max))
                return new("error", bucketName, null, null, null, null, null, null,
                    "Cloudflare Analytics trả mẫu dung lượng không đầy đủ.");

            var payloadSize = ReadMetric(max, "payloadSize");
            var metadataSize = ReadMetric(max, "metadataSize");
            var objectCount = ReadMetric(max, "objectCount");
            var pendingUploads = ReadMetric(max, "uploadCount");
            if (payloadSize is null || metadataSize is null || objectCount is null)
                return new("error", bucketName, null, payloadSize, metadataSize, objectCount, pendingUploads, null,
                    "Cloudflare Analytics không trả đủ trường dung lượng cho bucket.");

            DateTimeOffset? sampledAt = null;
            if (group.TryGetProperty("dimensions", out var dimensions)
                && dimensions.TryGetProperty("datetime", out var datetime)
                && datetime.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(datetime.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTime))
                sampledAt = parsedTime;

            return new("available", bucketName, payloadSize.Value + metadataSize.Value,
                payloadSize, metadataSize, objectCount, pendingUploads, sampledAt,
                "Đã đọc dung lượng theo bucket từ Cloudflare R2 Analytics.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new("error", bucketName, null, null, null, null, null, null,
                "Đọc số liệu Cloudflare R2 quá thời gian 3 giây.");
        }
        catch (HttpRequestException)
        {
            return new("error", bucketName, null, null, null, null, null, null,
                "Không kết nối được Cloudflare Analytics để đọc dung lượng R2.");
        }
        catch (JsonException)
        {
            return new("error", bucketName, null, null, null, null, null, null,
                "Cloudflare Analytics trả dữ liệu dung lượng không hợp lệ.");
        }
    }

    private async Task<AdminNeonUsage> GetNeonUsageAsync(CancellationToken ct)
    {
        long databaseSizeBytes;
        int activeConnections;
        int connectionLimit;
        try
        {
            databaseSizeBytes = await db.Database.SqlQuery<long>(
                $"SELECT pg_database_size(current_database())::bigint AS \"Value\"").SingleAsync(ct);
            activeConnections = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database() AND backend_type = 'client backend'
                """).SingleAsync(ct);
            connectionLimit = await db.Database.SqlQuery<int>($"""
                SELECT setting::integer AS "Value" FROM pg_settings WHERE name = 'max_connections'
                """).SingleAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new("error", null, null, null, null,
                "Không đọc được dung lượng database và giới hạn kết nối PostgreSQL.");
        }

        var apiKey = configuration["NEON_API_KEY"]?.Trim();
        var orgId = configuration["NEON_ORG_ID"]?.Trim();
        var projectId = configuration["NEON_PROJECT_ID"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(orgId)
            || string.IsNullOrWhiteSpace(projectId))
            return new("partial", databaseSizeBytes, activeConnections, connectionLimit, null,
                "Đã đo dung lượng database và kết nối từ PostgreSQL; usage CU tháng này cần NEON_API_KEY, NEON_ORG_ID và NEON_PROJECT_ID.");

        var cacheKey = $"admin-neon-usage:{orgId}:{projectId}";
        var consumption = await GetL2Async(cacheKey, NeonUsageCacheDuration,
            token => QueryNeonConsumptionAsync(apiKey, orgId, projectId, token), ct);

        return new(consumption?.Status ?? "error", databaseSizeBytes, activeConnections, connectionLimit,
            consumption?.ComputeUnitHours,
            consumption?.Detail ?? "Neon không trả dữ liệu consumption.");
    }

    private async Task<NeonConsumptionResult> QueryNeonConsumptionAsync(
        string apiKey, string orgId, string projectId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var query = new Dictionary<string, string>
        {
            ["from"] = monthStart.ToString("O", CultureInfo.InvariantCulture),
            ["to"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["granularity"] = "daily",
            ["org_id"] = orgId,
            ["project_ids"] = projectId,
            ["metrics"] = "compute_unit_seconds"
        };
        var uri = "https://console.neon.tech/api/v2/consumption_history/v2/projects?" +
            string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderMetricsTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClientFactory.CreateClient().SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden)
                return new("partial", null, "Neon API không cung cấp consumption cho gói hiện tại; dung lượng DB và kết nối vẫn được đo trực tiếp.");
            if (!response.IsSuccessStatusCode)
                return new("error", null, $"Neon Consumption API trả HTTP {(int)response.StatusCode}; kiểm tra quyền token, organization và project ID.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (!payload.RootElement.TryGetProperty("projects", out var projects)
                || projects.ValueKind != JsonValueKind.Array)
                return new("error", null, "Neon Consumption API trả cấu trúc dữ liệu không hợp lệ.");

            double totalComputeSeconds = 0;
            var foundMetric = false;
            foreach (var project in projects.EnumerateArray())
            {
                if (!project.TryGetProperty("project_id", out var returnedProject)
                    || !string.Equals(returnedProject.GetString(), projectId, StringComparison.Ordinal)) continue;
                if (!project.TryGetProperty("periods", out var periods) || periods.ValueKind != JsonValueKind.Array) continue;
                foreach (var period in periods.EnumerateArray())
                {
                    if (!period.TryGetProperty("consumption", out var consumption) || consumption.ValueKind != JsonValueKind.Array) continue;
                    foreach (var timeframe in consumption.EnumerateArray())
                    {
                        if (!timeframe.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array) continue;
                        foreach (var metric in metrics.EnumerateArray())
                        {
                            if (!metric.TryGetProperty("metric_name", out var metricName)
                                || !string.Equals(metricName.GetString(), "compute_unit_seconds", StringComparison.Ordinal)) continue;
                            var value = ReadMetric(metric, "value");
                            if (value is null) continue;
                            totalComputeSeconds += value.Value;
                            foundMetric = true;
                        }
                    }
                }
            }

            return foundMetric
                ? new("available", totalComputeSeconds / 3600d, "Đã đọc CU-hours tháng này từ Neon Consumption API; số liệu cập nhật theo chu kỳ của Neon.")
                : new("available", 0, "Neon chưa ghi nhận compute unit trong tháng này.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new("error", null, "Đọc Neon Consumption API quá thời gian 10 giây.");
        }
        catch (HttpRequestException)
        {
            return new("error", null, "Không kết nối được Neon Consumption API.");
        }
        catch (JsonException)
        {
            return new("error", null, "Neon Consumption API trả dữ liệu JSON không hợp lệ.");
        }
    }

    private async Task<AdminRedisUsage> GetRedisUsageAsync(CancellationToken ct)
    {
        var email = configuration["UPSTASH_EMAIL"]?.Trim();
        var apiKey = configuration["UPSTASH_API_KEY"]?.Trim();
        var databaseId = configuration["UPSTASH_REDIS_DATABASE_ID"]?.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(databaseId))
            return new("unmonitored", "", null, null, null, null, "",
                "Health check Redis đã dùng REST URL/token; quota yêu cầu UPSTASH_EMAIL, UPSTASH_API_KEY và UPSTASH_REDIS_DATABASE_ID.");

        var cacheKey = $"admin-upstash-usage:{databaseId}";
        return await GetL2Async(cacheKey, UpstashUsageCacheDuration,
            token => QueryUpstashUsageAsync(email, apiKey, databaseId, token), ct);
    }

    private async Task<AdminRedisUsage> QueryUpstashUsageAsync(
        string email, string apiKey, string databaseId, CancellationToken ct)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiKey}"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderMetricsTimeout);
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            var authorization = new AuthenticationHeaderValue("Basic", credentials);
            using var databaseRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.upstash.com/v2/redis/database/{Uri.EscapeDataString(databaseId)}?credentials=hide");
            databaseRequest.Headers.Authorization = authorization;
            using var databaseResponse = await client.SendAsync(databaseRequest,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (databaseResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new("error", "", null, null, null, null, "", "Upstash từ chối quyền đọc; cần API key chỉ đọc của tài khoản native Upstash.");
            if (!databaseResponse.IsSuccessStatusCode)
                return new("error", "", null, null, null, null, "", $"Upstash database API trả HTTP {(int)databaseResponse.StatusCode}.");

            await using var databaseStream = await databaseResponse.Content.ReadAsStreamAsync(timeout.Token);
            using var database = await JsonDocument.ParseAsync(databaseStream, cancellationToken: timeout.Token);
            var root = database.RootElement;
            var name = ReadText(root, "database_name") ?? "Upstash Redis";
            var plan = ReadText(root, "type") ?? "";
            var storageLimit = ReadMetric(root, "db_disk_threshold");
            var requestLimit = ReadMetric(root, "db_request_limit");

            using var statsRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.upstash.com/v2/redis/stats/{Uri.EscapeDataString(databaseId)}");
            statsRequest.Headers.Authorization = authorization;
            using var statsResponse = await client.SendAsync(statsRequest,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!statsResponse.IsSuccessStatusCode)
                return new("error", name, null, storageLimit, null, requestLimit, plan,
                    $"Upstash stats API trả HTTP {(int)statsResponse.StatusCode}.");

            await using var statsStream = await statsResponse.Content.ReadAsStreamAsync(timeout.Token);
            using var stats = await JsonDocument.ParseAsync(statsStream, cancellationToken: timeout.Token);
            var statsRoot = stats.RootElement;
            return new("available", name, ReadMetric(statsRoot, "current_storage"), storageLimit,
                ReadMetric(statsRoot, "total_monthly_requests"), requestLimit, plan,
                "Dung lượng hiện tại, hạn mức database và request tháng này lấy từ Upstash Developer API.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new("error", "", null, null, null, null, "", "Đọc Upstash Developer API quá thời gian 10 giây.");
        }
        catch (HttpRequestException)
        {
            return new("error", "", null, null, null, null, "", "Không kết nối được Upstash Developer API.");
        }
        catch (JsonException)
        {
            return new("error", "", null, null, null, null, "", "Upstash Developer API trả dữ liệu JSON không hợp lệ.");
        }
    }

    private async Task RecordSignalTransitionsAsync(
        IReadOnlyList<AdminSystemSignal> signals, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (transaction != null)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"admin-system-monitor:signals"}, 0))", ct);

        foreach (var signal in signals)
        {
            var previousJson = await db.AuditLogs.AsNoTracking()
                .Where(log => log.ResourceType == "system_signal" && log.Reason == signal.Key
                    && log.Action.StartsWith("system.signal."))
                .OrderByDescending(log => log.CreatedAt).ThenByDescending(log => log.AuditLogId)
                .Select(log => log.NewValues).FirstOrDefaultAsync(ct);
            var previousState = ReadSignalState(previousJson);
            var currentState = signal.IsTriggered ? signal.Severity : "normal";
            if (previousState == currentState || (previousState is null && currentState == "normal")) continue;

            var action = previousState is null or "normal" ? "raised"
                : currentState == "normal" ? "resolved"
                : SeverityRank(currentState) > SeverityRank(previousState) ? "escalated" : "changed";
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = null,
                Action = $"system.signal.{action}",
                ResourceType = "system_signal",
                Reason = signal.Key,
                OldValues = previousState is null ? null : JsonSerializer.Serialize(new { status = previousState }),
                NewValues = JsonSerializer.Serialize(new
                {
                    status = currentState,
                    title = signal.Title,
                    description = signal.Description,
                    currentValue = signal.CurrentValue,
                    signal.Unit,
                    signal.Threshold
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                CreatedAt = now
            });
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
    }

    private async Task<IReadOnlyList<AdminSystemActivity>> GetIncidentHistoryAsync(CancellationToken ct)
    {
        var events = await db.AuditLogs.AsNoTracking()
            .Where(log => log.ResourceType == "system_signal" && log.Action.StartsWith("system.signal."))
            .OrderByDescending(log => log.CreatedAt).ThenByDescending(log => log.AuditLogId).Take(20)
            .Select(log => new { log.Action, log.Reason, log.NewValues, log.CreatedAt })
            .ToListAsync(ct);
        return events.Select(item =>
        {
            using var payload = TryParseJson(item.NewValues);
            var root = payload?.RootElement;
            var title = ReadText(root, "title") ?? item.Reason ?? "Tín hiệu hệ thống";
            var description = ReadText(root, "description") ?? "Trạng thái tín hiệu đã thay đổi.";
            var status = ReadText(root, "status") ?? (item.Action.EndsWith("resolved", StringComparison.Ordinal) ? "normal" : "warning");
            return new AdminSystemActivity(item.CreatedAt, "incident", status, title,
                $"{ActionLabel(item.Action)} · {description}", null);
        }).ToList();
    }

    private async Task<IReadOnlyList<AdminSystemActivity>> GetRecentActivityAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var logs = await db.AuditLogs.AsNoTracking()
            .Where(log => log.CreatedAt >= since && (log.ActorUserId != null
                || log.ResourceType == "payment_webhook"
                || log.Action.StartsWith("video.upload.")
                || log.Action.StartsWith("video.rendition.processing.")))
            .OrderByDescending(log => log.CreatedAt).ThenByDescending(log => log.AuditLogId).Take(40)
            .Select(log => new { log.Action, log.ResourceType, log.ResourceId, log.ActorUserId, log.Reason, log.CreatedAt })
            .ToListAsync(ct);

        var items = logs.Select(log => new AdminSystemActivity(log.CreatedAt,
            log.ResourceType == "payment_webhook" ? "payment"
                : log.Action.StartsWith("video.", StringComparison.Ordinal) ? "upload"
                : log.ActorUserId is null ? "system" : "admin",
            IsFailureAction(log.Action) ? "error" : "info",
            ActivityTitle(log.Action), log.Reason ?? log.Action, log.ResourceId)).ToList();

        var failedVideos = await db.Videos.AsNoTracking()
            .Where(video => video.Status == "failed" && video.UpdatedAt >= since)
            .OrderByDescending(video => video.UpdatedAt).Take(10)
            .Select(video => new { video.VideoId, video.Title, video.UpdatedAt })
            .ToListAsync(ct);
        items.AddRange(failedVideos.Select(video => new AdminSystemActivity(video.UpdatedAt, "upload", "error",
            "Xử lý video thất bại", video.Title, video.VideoId)));

        var failedRenditions = await (
            from rendition in db.VideoRenditions.AsNoTracking()
            join video in db.Videos.AsNoTracking() on rendition.VideoId equals video.VideoId
            where rendition.Status == "failed" && rendition.UpdatedAt >= since
            orderby rendition.UpdatedAt descending
            select new { rendition.VideoId, rendition.QualityLabel, rendition.UpdatedAt, video.Title }
        ).Take(10).ToListAsync(ct);
        items.AddRange(failedRenditions.Select(rendition => new AdminSystemActivity(rendition.UpdatedAt,
            "upload", "error", "Tạo video rendition thất bại",
            $"{rendition.Title} · {rendition.QualityLabel}", rendition.VideoId)));

        var failedPayments = await db.Payments.AsNoTracking()
            .Where(payment => (payment.Status == "failed" || payment.Status == "cancelled") && payment.UpdatedAt >= since)
            .OrderByDescending(payment => payment.UpdatedAt).Take(10)
            .Select(payment => new { payment.PaymentId, payment.TransactionCode, payment.Status, payment.UpdatedAt })
            .ToListAsync(ct);
        items.AddRange(failedPayments.Select(payment => new AdminSystemActivity(payment.UpdatedAt,
            "payment", payment.Status == "failed" ? "error" : "info",
            payment.Status == "failed" ? "Thanh toán thất bại" : "Thanh toán đã hủy",
            payment.TransactionCode, payment.PaymentId)));

        return items.OrderByDescending(item => item.OccurredAt).Take(20).ToList();
    }

    private static bool IsFailureAction(string action) =>
        action.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || action.Contains("rejected", StringComparison.OrdinalIgnoreCase)
        || action.Contains("error", StringComparison.OrdinalIgnoreCase);

    private static string ActivityTitle(string action) => action switch
    {
        "payment.sepay_webhook.processed" => "SePay webhook đã xử lý",
        "payment.sepay_webhook.rejected" => "SePay webhook bị từ chối",
        "payment.sepay_webhook.failed" => "SePay webhook xử lý lỗi",
        "video.upload.failed" => "Upload video thất bại",
        "video.rendition.processing.failed" => "Tạo rendition thất bại",
        _ => action
    };

    private static string ActionLabel(string action) => action switch
    {
        "system.signal.raised" => "Đã phát hiện",
        "system.signal.escalated" => "Đã nâng mức",
        "system.signal.changed" => "Đã đổi trạng thái",
        "system.signal.resolved" => "Đã xử lý",
        _ => "Cập nhật"
    };

    private static int SeverityRank(string? severity) => severity switch
    {
        "critical" => 2,
        "warning" => 1,
        _ => 0
    };

    private static string? ReadSignalState(string? json)
    {
        using var payload = TryParseJson(json);
        return ReadText(payload?.RootElement, "status");
    }

    private static JsonDocument? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string? ReadText(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } root
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? ToBytes(double? value, string? unit)
    {
        if (value is null || !double.IsFinite(value.Value) || value.Value < 0) return null;
        var multiplier = unit?.Trim().ToUpperInvariant() switch
        {
            "B" or "BYTE" or "BYTES" => 1d,
            "KB" => 1_000d,
            "MB" => 1_000_000d,
            "GB" => 1_000_000_000d,
            "TB" => 1_000_000_000_000d,
            _ => 1d
        };
        var bytes = value.Value * multiplier;
        return bytes <= long.MaxValue ? (long)Math.Round(bytes) : null;
    }

    private static string CompactProviderError(string value)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 240 ? compact : compact[..240] + "…";
    }

    private sealed record NeonConsumptionResult(string Status, double? ComputeUnitHours, string Detail);
    private static long? ReadMetric(JsonElement metrics, string name)
    {
        if (!metrics.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (value.TryGetInt64(out var integer)) return integer;
        return value.TryGetDecimal(out var decimalValue) && decimalValue <= long.MaxValue && decimalValue >= long.MinValue
            ? decimal.ToInt64(decimal.Round(decimalValue))
            : null;
    }

    private static double? ReadMetricDouble(JsonElement metrics, string name)
    {
        if (!metrics.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number)) return null;
        return number;
    }

    private async Task<HttpProbeResult> ProbeHttpAsync(string baseUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            return new(false, 0, 0);

        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HealthProbeTimeout);
        try
        {
            using var response = await httpClientFactory.CreateClient().GetAsync(uri,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return new(response.IsSuccessStatusCode, (int)response.StatusCode, timer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, 0, HealthProbeTimeout.TotalMilliseconds); }
        catch (HttpRequestException) { return new(false, 0, timer.Elapsed.TotalMilliseconds); }
    }

    private sealed record HttpProbeResult(bool IsHealthy, int StatusCode, double ResponseTimeMs);

    public async Task<AdminSystemReportResponse> GetReportAsync(int days, CancellationToken ct)
    {
        days = days is 7 or 30 or 90 ? days : 30;
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1 - days), TimeSpan.Zero);

        var usersQuery = db.Users.AsNoTracking();
        var verifiedUsersQuery = usersQuery.Where(user => user.EmailVerifiedAt != null);
        var videosQuery = db.Videos.AsNoTracking();
        var publishedVideosQuery = videosQuery.Where(video => video.Status == "published");
        var channelsQuery = db.Channels.AsNoTracking().Where(channel => channel.Status != "deleted");
        var viewsQuery = db.ViewingHistories.AsNoTracking();
        var commentsQuery = db.Comments.AsNoTracking();
        var likesQuery = db.VideoReactions.AsNoTracking().Where(reaction => reaction.Type == "like");
        var subscribersQuery = db.Subscriptions.AsNoTracking().Where(subscription => subscription.Status == "active");
        var reportsQuery = db.Reports.AsNoTracking();
        var paymentsQuery = db.Payments.AsNoTracking();

        var userTotal = await usersQuery.LongCountAsync(ct);
        var usersInPeriod = await usersQuery.LongCountAsync(user => user.CreatedAt >= start && user.CreatedAt < now, ct);
        var verifiedTotal = await verifiedUsersQuery.LongCountAsync(ct);
        var verifiedInPeriod = await verifiedUsersQuery.LongCountAsync(user => user.EmailVerifiedAt >= start && user.EmailVerifiedAt < now, ct);
        var videoTotal = await videosQuery.LongCountAsync(ct);
        var videoInPeriod = await videosQuery.LongCountAsync(video => video.CreatedAt >= start && video.CreatedAt < now, ct);
        var publishedTotal = await publishedVideosQuery.LongCountAsync(ct);
        var publishedInPeriod = await publishedVideosQuery.LongCountAsync(video => video.PublishedAt >= start && video.PublishedAt < now, ct);
        var channelTotal = await channelsQuery.LongCountAsync(ct);
        var channelInPeriod = await channelsQuery.LongCountAsync(channel => channel.CreatedAt >= start && channel.CreatedAt < now, ct);
        var viewsTotal = await viewsQuery.LongCountAsync(ct);
        var viewsInPeriod = await viewsQuery.LongCountAsync(view => view.ViewedAt >= start && view.ViewedAt < now, ct);
        var commentsTotal = await commentsQuery.LongCountAsync(ct);
        var commentsInPeriod = await commentsQuery.LongCountAsync(comment => comment.CreatedAt >= start && comment.CreatedAt < now, ct);
        var likesTotal = await likesQuery.LongCountAsync(ct);
        var likesInPeriod = await likesQuery.LongCountAsync(reaction => reaction.CreatedAt >= start && reaction.CreatedAt < now, ct);
        var subscribersTotal = await subscribersQuery.LongCountAsync(ct);
        var subscribersInPeriod = await subscribersQuery.LongCountAsync(subscription => subscription.SubscribedAt >= start && subscription.SubscribedAt < now, ct);
        var reportsTotal = await reportsQuery.LongCountAsync(ct);
        var reportsInPeriod = await reportsQuery.LongCountAsync(report => report.CreatedAt >= start && report.CreatedAt < now, ct);
        var pendingReports = reportsQuery.Where(report => report.Status == "pending");
        var pendingReportsTotal = await pendingReports.LongCountAsync(ct);
        var pendingReportsInPeriod = await pendingReports.LongCountAsync(report => report.CreatedAt >= start && report.CreatedAt < now, ct);
        var pendingReviews = db.ModerationCases.AsNoTracking().Where(item => item.CaseType == "upload_review" && item.Status == "pending");
        var pendingReviewsTotal = await pendingReviews.LongCountAsync(ct);
        var pendingReviewsInPeriod = await pendingReviews.LongCountAsync(item => item.SubmittedAt >= start && item.SubmittedAt < now, ct);
        var pendingAppeals = db.Appeals.AsNoTracking().Where(appeal => appeal.Status == "pending" || appeal.Status == "reviewing");
        var pendingAppealsTotal = await pendingAppeals.LongCountAsync(ct);
        var pendingAppealsInPeriod = await pendingAppeals.LongCountAsync(appeal => appeal.CreatedAt >= start && appeal.CreatedAt < now, ct);
        var activeSessions = db.Sessions.AsNoTracking().Where(session => session.RevokedAt == null && session.ExpiresAt > now);
        var activeSessionsTotal = await activeSessions.LongCountAsync(ct);
        var activeSessionsInPeriod = await activeSessions.LongCountAsync(session => session.IssuedAt >= start, ct);
        var shares = db.ShareHistories.AsNoTracking();
        var sharesTotal = await shares.LongCountAsync(ct);
        var sharesInPeriod = await shares.LongCountAsync(share => share.SharedAt >= start && share.SharedAt < now, ct);

        var paidPayments = paymentsQuery.Where(payment => payment.Status == "paid");
        var totalRevenue = await paidPayments.SumAsync(payment => (decimal?)payment.Amount, ct) ?? 0;
        var transactionsTotal = await paidPayments.LongCountAsync(ct);
        var paidInPeriod = paidPayments.Where(payment => payment.PaidAt >= start && payment.PaidAt < now);
        var periodRevenue = await paidInPeriod.SumAsync(payment => (decimal?)payment.Amount, ct) ?? 0;
        var transactionsInPeriod = await paidInPeriod.LongCountAsync(ct);

        var usersByDay = await usersQuery.Where(user => user.CreatedAt >= start && user.CreatedAt < now)
            .GroupBy(user => new { user.CreatedAt.Year, user.CreatedAt.Month, user.CreatedAt.Day })
            .Select(group => new { group.Key.Year, group.Key.Month, group.Key.Day, Count = group.LongCount() }).ToListAsync(ct);
        var videosByDay = await videosQuery.Where(video => video.CreatedAt >= start && video.CreatedAt < now)
            .GroupBy(video => new { video.CreatedAt.Year, video.CreatedAt.Month, video.CreatedAt.Day })
            .Select(group => new { group.Key.Year, group.Key.Month, group.Key.Day, Count = group.LongCount() }).ToListAsync(ct);
        var viewsByDay = await viewsQuery.Where(view => view.ViewedAt >= start && view.ViewedAt < now)
            .GroupBy(view => new { view.ViewedAt.Year, view.ViewedAt.Month, view.ViewedAt.Day })
            .Select(group => new { group.Key.Year, group.Key.Month, group.Key.Day, Count = group.LongCount() }).ToListAsync(ct);
        var commentsByDay = await commentsQuery.Where(comment => comment.CreatedAt >= start && comment.CreatedAt < now)
            .GroupBy(comment => new { comment.CreatedAt.Year, comment.CreatedAt.Month, comment.CreatedAt.Day })
            .Select(group => new { group.Key.Year, group.Key.Month, group.Key.Day, Count = group.LongCount() }).ToListAsync(ct);
        var revenueByDay = await paidInPeriod.Where(payment => payment.PaidAt != null)
            .GroupBy(payment => new { payment.PaidAt!.Value.Year, payment.PaidAt.Value.Month, payment.PaidAt.Value.Day })
            .Select(group => new { group.Key.Year, group.Key.Month, group.Key.Day, Amount = group.Sum(payment => (decimal?)payment.Amount) ?? 0 })
            .ToListAsync(ct);

        var userLookup = usersByDay.ToDictionary(row => new DateOnly(row.Year, row.Month, row.Day), row => row.Count);
        var videoLookup = videosByDay.ToDictionary(row => new DateOnly(row.Year, row.Month, row.Day), row => row.Count);
        var viewLookup = viewsByDay.ToDictionary(row => new DateOnly(row.Year, row.Month, row.Day), row => row.Count);
        var commentLookup = commentsByDay.ToDictionary(row => new DateOnly(row.Year, row.Month, row.Day), row => row.Count);
        var revenueLookup = revenueByDay.ToDictionary(row => new DateOnly(row.Year, row.Month, row.Day), row => row.Amount);
        var daily = Enumerable.Range(0, days).Select(offset =>
        {
            var date = DateOnly.FromDateTime(start.UtcDateTime.Date.AddDays(offset));
            return new AdminSystemReportDay(date, userLookup.GetValueOrDefault(date), videoLookup.GetValueOrDefault(date),
                viewLookup.GetValueOrDefault(date), commentLookup.GetValueOrDefault(date), revenueLookup.GetValueOrDefault(date));
        }).ToList();

        var videoStatusCounts = await videosQuery.GroupBy(video => video.Status)
            .Select(group => new { Key = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count).ToListAsync(ct);
        var videoStatuses = videoStatusCounts
            .Select(item => new AdminSystemReportSlice(item.Key, item.Count)).ToList();
        var reportStatusCounts = await reportsQuery.GroupBy(report => report.Status)
            .Select(group => new { Key = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count).ToListAsync(ct);
        var reportStatuses = reportStatusCounts
            .Select(item => new AdminSystemReportSlice(item.Key, item.Count)).ToList();
        var paymentMethodCounts = await paymentsQuery.Where(payment => payment.Status == "paid")
            .GroupBy(payment => payment.PaymentMethod)
            .Select(group => new { Key = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count).ToListAsync(ct);
        var paymentMethods = paymentMethodCounts
            .Select(item => new AdminSystemReportSlice(item.Key, item.Count)).ToList();

        return new AdminSystemReportResponse(now, days,
            new(userTotal, usersInPeriod), new(verifiedTotal, verifiedInPeriod),
            new(videoTotal, videoInPeriod), new(publishedTotal, publishedInPeriod), new(channelTotal, channelInPeriod),
            new(viewsTotal, viewsInPeriod), new(commentsTotal, commentsInPeriod), new(likesTotal, likesInPeriod),
            new(subscribersTotal, subscribersInPeriod), new(reportsTotal, reportsInPeriod),
            new(pendingReportsTotal, pendingReportsInPeriod), new(pendingReviewsTotal, pendingReviewsInPeriod),
            new(pendingAppealsTotal, pendingAppealsInPeriod), new(activeSessionsTotal, activeSessionsInPeriod),
            new(sharesTotal, sharesInPeriod), new(totalRevenue, transactionsTotal, periodRevenue, transactionsInPeriod),
            daily, videoStatuses, reportStatuses, paymentMethods);
    }

    public async Task<AdminAuditLogPage> GetAuditLogsAsync(
        int page, int pageSize, string? search, string? action, DateTimeOffset? fromDate, DateTimeOffset? toDate, CancellationToken ct)
    {
        page = Math.Clamp(page, 1, 10_000_000);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var now = DateTimeOffset.UtcNow;
        var since = now.AddHours(-24);
        var allLogs = db.AuditLogs.AsNoTracking();
        var query = from log in allLogs
                    join actor in db.Users.IgnoreQueryFilters().AsNoTracking() on log.ActorUserId equals actor.UserId into actorGroup
                    from actor in actorGroup.DefaultIfEmpty()
                    select new { Log = log, ActorName = actor == null ? null : actor.DisplayName, ActorEmail = actor == null ? null : actor.Email };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var exactIp = System.Net.IPAddress.TryParse(term, out var parsedIp) ? parsedIp.ToString() : null;
            query = query.Where(item => item.Log.Action.Contains(term)
                || (item.Log.ResourceType != null && item.Log.ResourceType.Contains(term))
                || (item.Log.Reason != null && item.Log.Reason.Contains(term))
                || (item.ActorName != null && item.ActorName.Contains(term))
                || (item.ActorEmail != null && item.ActorEmail.Contains(term))
                || (exactIp != null && item.Log.IpAddress == exactIp));
        }
        if (!string.IsNullOrWhiteSpace(action) && action != "all") query = query.Where(item => item.Log.Action == action);
        var fromUtc = fromDate?.ToUniversalTime();
        var toUtc = toDate?.ToUniversalTime();
        if (fromUtc.HasValue) query = query.Where(item => item.Log.CreatedAt >= fromUtc.Value);
        if (toUtc.HasValue) query = query.Where(item => item.Log.CreatedAt < toUtc.Value);

        var total = await query.LongCountAsync(ct);
        var items = await query.OrderByDescending(item => item.Log.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new AdminAuditLogItem(
                item.Log.AuditLogId, item.Log.ActorUserId, item.ActorName, item.ActorEmail,
                item.Log.Action, item.Log.ResourceType, item.Log.ResourceId, item.Log.Reason,
                item.Log.IpAddress, item.Log.UserAgent, item.Log.CreatedAt))
            .ToListAsync(ct);

        var eventsLast24Hours = await allLogs.LongCountAsync(log => log.CreatedAt >= since && log.CreatedAt < now, ct);
        var activeAdministrators = await allLogs.Where(log => log.CreatedAt >= since && log.CreatedAt < now && log.ActorUserId != null)
            .Select(log => log.ActorUserId!.Value).Distinct().LongCountAsync(ct);
        var loginsLast24Hours = await allLogs.LongCountAsync(log => log.CreatedAt >= since && log.CreatedAt < now && log.Action == "admin.login", ct);
        var actions = await allLogs.Select(log => log.Action).Distinct().OrderBy(actionName => actionName).Take(100).ToListAsync(ct);
        var runtimeSettings = BuildRuntimeSettings();
        return new AdminAuditLogPage(page, pageSize, total, eventsLast24Hours, activeAdministrators, loginsLast24Hours,
            actions, runtimeSettings, items);
    }

    private IReadOnlyList<AdminSystemRuntimeSetting> BuildRuntimeSettings()
    {
        var rateLimit = configuration.GetValue("RateLimit:AuthPermitLimit", 60);
        var signupThreshold = Math.Max(1, configuration.GetValue("SystemAlerts:SignupBurstPerHour", 20));
        var reviewThreshold = Math.Max(1, configuration.GetValue("SystemAlerts:PendingReviewThreshold", 50));
        var paymentFailureThreshold = Math.Max(1, configuration.GetValue("SystemAlerts:FailedPaymentsPerDay", 10));
        var apiErrorThreshold = Math.Max(1, configuration.GetValue("SystemAlerts:ApiErrorsPer15Minutes", 5));
        var memoryWarningThreshold = Math.Clamp(configuration.GetValue("SystemAlerts:MemoryWarningPercent", 75), 1, 99);
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var (memoryPercent, workingSetBytes, availableBytes) = ReadMemoryUsage();
        var memoryDisplay = memoryPercent is int currentMemoryPercent ? $"{currentMemoryPercent}%" : "không có số đo";
        return
        [
            new("Môi trường ứng dụng", environment.EnvironmentName, "ASPNETCORE_ENVIRONMENT"),
            new("API", "v1", "Định tuyến /api/v1"),
            new("Phiên bản backend", version, "Assembly đang chạy"),
            new("Giới hạn xác thực", $"{rateLimit} yêu cầu/phút/IP", "RateLimit:AuthPermitLimit"),
            new("Ngưỡng cảnh báo đăng ký", $"{signupThreshold} tài khoản/giờ", "SystemAlerts:SignupBurstPerHour"),
            new("Ngưỡng hàng đợi kiểm duyệt", $"{reviewThreshold} video", "SystemAlerts:PendingReviewThreshold"),
            new("Ngưỡng giao dịch lỗi", $"{paymentFailureThreshold} giao dịch/24 giờ", "SystemAlerts:FailedPaymentsPerDay"),
            new("Ngưỡng lỗi API", $"{apiErrorThreshold} lỗi/15 phút", "SystemAlerts:ApiErrorsPer15Minutes"),
            new("Cảnh báo bộ nhớ", $"{memoryWarningThreshold}% (hiện tại {memoryDisplay})", "SystemAlerts:MemoryWarningPercent"),
            new("Bộ nhớ tiến trình", $"{FormatBytes(workingSetBytes)} / {FormatBytes(availableBytes)}", "Process.WorkingSet64 / GC memory limit")
        ];
    }

    private static (int? Percent, long? WorkingSet, long? Available) ReadMemoryUsage()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var workingSet = process.WorkingSet64;
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available <= 0) return (null, workingSet, available);
            return ((int)Math.Clamp(Math.Round(workingSet * 100d / available), 0, 100), workingSet, available);
        }
        catch (Exception) { return (null, null, null); }
    }

    private static AdminEc2MemoryUsage ReadEc2MemoryUsage(DateTimeOffset sampledAt)
    {
        if (!OperatingSystem.IsLinux())
            return new("unmonitored", null, null, null, null, null,
                "RAM host được đọc từ /proc/meminfo trên Linux EC2.");

        try
        {
            long? totalKilobytes = null;
            long? availableKilobytes = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var separator = line.IndexOf(':');
                if (separator < 0) continue;
                var key = line[..separator];
                if (key is not ("MemTotal" or "MemAvailable")) continue;
                var fields = line[(separator + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 0 || !long.TryParse(fields[0], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var kilobytes) || kilobytes < 0) continue;
                if (key == "MemTotal") totalKilobytes = kilobytes;
                else availableKilobytes = kilobytes;
            }

            if (totalKilobytes is not > 0 || availableKilobytes is null)
                return new("error", null, null, null, null, null,
                    "Không tìm thấy MemTotal và MemAvailable trong /proc/meminfo.");

            var totalBytes = checked(totalKilobytes.Value * 1024);
            var availableBytes = Math.Clamp(checked(availableKilobytes.Value * 1024), 0, totalBytes);
            var usedBytes = totalBytes - availableBytes;
            var usedPercent = (int)Math.Round(usedBytes * 100d / totalBytes);
            return new("available", totalBytes, usedBytes, availableBytes, usedPercent, sampledAt,
                "RAM của máy EC2 đọc trực tiếp từ /proc/meminfo trên máy chủ Linux.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return new("error", null, null, null, null, null,
                "Không thể đọc RAM host từ /proc/meminfo trong container API.");
        }
    }

    private static string FormatBytes(long? value) => value switch
    {
        null or <= 0 => "không xác định",
        >= 1_073_741_824 => $"{value / 1_073_741_824d:0.0} GB",
        _ => $"{value / 1_048_576d:0} MB"
    };
}
