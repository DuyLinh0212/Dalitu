using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HuTube.Application.Payments;
using HuTube.Application.Rbac;
using HuTube.Infrastructure.Caching;
using HuTube.Infrastructure.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HuTube.Api.Controllers;

[ApiController]
[Route("api/v1/payments")]
public sealed class PaymentsController(
    IPaymentService paymentService,
    IServiceScopeFactory scopeFactory,
    UpstashRedisClient redis,
    IHostEnvironment environment,
    SepaySignatureVerifier signatureVerifier,
    ILogger<PaymentsController> logger) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
        ? id
        : throw new PaymentException(401, "UNAUTHORIZED", "Vui lòng đăng nhập để tiếp tục.");

    /// <summary>
    /// Khởi tạo đơn hàng thanh toán mua gói trả phí bằng SePay.
    /// </summary>
    [Authorize]
    [HttpPost, EnableRateLimiting("payment-initiate")]
    public async Task<ActionResult<CreatePaymentResponse>> InitiateAsync(
        [FromBody] CreatePaymentRequest request,
        CancellationToken ct)
    {
        var response = await paymentService.InitiateAsync(UserId, request, ct);
        return Ok(response);
    }

    /// <summary>
    /// Lấy danh sách lịch sử giao dịch thanh toán của người dùng hiện tại.
    /// </summary>
    [Authorize]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PaymentSummaryResponse>>> GetMyPaymentsAsync(CancellationToken ct)
    {
        var result = await paymentService.GetUserPaymentsAsync(UserId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Lấy chi tiết/trạng thái một giao dịch thanh toán (dùng cho client polling kết quả).
    /// </summary>
    [Authorize]
    [HttpGet("{paymentId:guid}")]
    public async Task<ActionResult<PaymentSummaryResponse>> GetPaymentAsync(Guid paymentId, CancellationToken ct)
    {
        var result = await paymentService.GetPaymentAsync(UserId, paymentId, ct);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [Authorize]
    [HttpPost("{paymentId:guid}/cancel")]
    public async Task<IActionResult> CancelAsync(Guid paymentId, CancellationToken ct)
    {
        await paymentService.CancelAsync(UserId, paymentId, ct);
        return NoContent();
    }

    /// <summary>
    /// Webhook nhận thông báo chuyển khoản tự động từ SePay.
    /// SePay yêu cầu trả về HTTP 200/201 với body {"success": true}.
    /// </summary>
    [HttpPost("webhook/sepay"), EnableRateLimiting("payment-webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleSepayWebhookAsync(CancellationToken ct)
    {
        // 1. Đọc raw body bytes để verify chữ ký HMAC-SHA256
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var bodyString = await reader.ReadToEndAsync(ct);
        var rawBytes = Encoding.UTF8.GetBytes(bodyString);

        var signature = Request.Headers["X-SePay-Signature"].FirstOrDefault();
        var timestamp = Request.Headers["X-SePay-Timestamp"].FirstOrDefault();

        // 2. Xác thực chữ ký nếu cấu hình SecretKey đã có
        if (!signatureVerifier.Verify(rawBytes, signature, timestamp))
        {
            logger.LogWarning("SePay webhook signature verification failed.");
            if (await TryRecordInvalidSignatureSampleAsync(ct))
                await RecordWebhookEventAsync("payment.sepay_webhook.rejected", "Chữ ký webhook SePay không hợp lệ.", ct);
            return Unauthorized(new { success = false, message = "Invalid signature." });
        }

        // 3. Deserialize payload
        SepayWebhookPayload? payload;
        try
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            payload = JsonSerializer.Deserialize<SepayWebhookPayload>(bodyString, jsonOptions);
            if (payload == null)
            {
                await RecordWebhookEventAsync("payment.sepay_webhook.rejected", "SePay gửi payload rỗng.", ct);
                return BadRequest(new { success = false, message = "Empty payload." });
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize SePay webhook payload.");
            await RecordWebhookEventAsync("payment.sepay_webhook.rejected", "Payload SePay không phải JSON hợp lệ.", ct);
            return BadRequest(new { success = false, message = "Malformed JSON." });
        }

        // 4. Xử lý logic nạp tiền / kích hoạt gói
        try
        {
            var handled = await paymentService.HandleSepayWebhookAsync(payload, ct);
            await RecordWebhookEventAsync(
                handled ? "payment.sepay_webhook.processed" : "payment.sepay_webhook.rejected",
                handled ? $"SePay transaction {payload.Id} đã qua bước xử lý webhook." : "Cấu hình hoặc thông tin tài khoản SePay không hợp lệ.", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing SePay payment for transaction {TransactionId}.", payload.Id);
            await RecordWebhookEventAsync("payment.sepay_webhook.failed",
                $"SePay transaction {payload.Id} lỗi xử lý ({ex.GetType().Name}).", ct);
            // Vẫn trả success: true nếu lỗi nội bộ không khắc phục được bằng retry ngay,
            // hoặc trả 500 nếu muốn SePay retry (SePay retry tối đa 7 lần).
            return StatusCode(500, new { success = false, message = "Internal processing error." });
        }

        // SePay yêu cầu response đúng định dạng này
        return Ok(new { success = true });
    }

    private async Task RecordWebhookEventAsync(string action, string reason, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var rbacService = scope.ServiceProvider.GetRequiredService<RbacService>();
            await rbacService.LogAuditAsync(new AuditLogEntry(null, action, "payment_webhook", Reason: reason), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not persist SePay webhook history for action {Action}.", action);
        }
    }

    private async Task<bool> TryRecordInvalidSignatureSampleAsync(CancellationToken ct)
    {
        if (!redis.Configured) return true;
        var key = $"dalitu:{environment.EnvironmentName.ToLowerInvariant()}:cache:payments:sepay-invalid-signature:v1";
        try
        {
            var result = await redis.CommandAsync(["SET", key, "1", "NX", "EX", "60"], ct);
            return result.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(result.GetString(), "OK", StringComparison.Ordinal);
        }
        catch (RedisUnavailableException)
        {
            logger.LogDebug("Redis cache unavailable while sampling invalid payment webhook signatures.");
            return true;
        }
    }
}
