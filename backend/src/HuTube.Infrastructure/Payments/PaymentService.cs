using System.Globalization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using HuTube.Application.Payments;
using HuTube.Application.Plans;
using HuTube.Domain.Payments;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HuTube.Infrastructure.Payments;

public sealed class PaymentService(
    HuTubeDbContext db,
    IPlanService planService,
    SepayOptions options,
    TimeProvider clock,
    PaymentExpiryWakeup? expiryWakeup = null) : IPaymentService
{
    private DateTimeOffset Now => clock.GetUtcNow();

    // ─── InitiateAsync ────────────────────────────────────────────────────────

    public async Task<CreatePaymentResponse> InitiateAsync(Guid userId, CreatePaymentRequest request, CancellationToken ct = default)
    {
        var (payment, planName) = await CreatePendingPaymentAsync(userId, request, ct);
        return BuildResponse(payment, planName);
    }

    private async Task<(Payment Payment, string PlanName)> CreatePendingPaymentAsync(
        Guid userId,
        CreatePaymentRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.SecretKey)
            || string.IsNullOrWhiteSpace(options.AccountNumber) || string.IsNullOrWhiteSpace(options.BankCode))
            throw new PaymentException(503, "PAYMENT_UNAVAILABLE", "Thanh toán chưa được cấu hình.");
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        if (key?.Length > 200)
            throw new PaymentException(400, "INVALID_IDEMPOTENCY_KEY", "Mã yêu cầu tối đa 200 ký tự.");
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (transaction != null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"plan-membership:" + userId}, 0))", ct);
        if ((await planService.GetMyPlanAsync(userId, ct))?.IsSharedMember == true)
            throw new PaymentException(403, "SHARED_PLAN_ACTION_FORBIDDEN", "Bạn đang sử dụng gói được chia sẻ và không thể mua gói khác.");
        // Validate gói tồn tại và có giá > 0
        var plan = await db.Plans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PlanId == request.PlanId && x.Status == "active", ct)
            ?? throw new PaymentException(404, "PLAN_NOT_FOUND", "Gói dịch vụ không tồn tại.");

        if (plan.Price <= 0)
            throw new PaymentException(400, "FREE_PLAN_NO_PAYMENT", "Gói miễn phí không cần thanh toán. Vui lòng dùng endpoint đăng ký trực tiếp.");
        var now = Now;

        // Idempotency: nếu cùng key + user + plan → trả về payment cũ (nếu còn pending và chưa hết hạn)
        if (key != null)
        {
            var existing = await db.Payments
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.IdempotencyKey == key && x.UserId == userId, ct);

            if (existing != null && (existing.PlanId != request.PlanId || existing.AutoRenew != request.AutoRenew
                || existing.PaymentMethod != "sepay"))
                throw new PaymentException(409, "IDEMPOTENCY_CONFLICT", "Mã yêu cầu đã dùng cho gói khác.");
            if (existing != null)
            {
                if (existing.Status == "pending" && existing.ExpiresAt > now)
                    return (existing, plan.Name);
                throw new PaymentException(409, "PAYMENT_REQUEST_ALREADY_PROCESSED", "Yêu cầu thanh toán này đã được xử lý hoặc hết hạn. Vui lòng tạo yêu cầu mới.");
            }
        }

        // Reuse an active attempt for the same purchase so repeated clicks do not
        // create multiple pending transfers for one user and plan.
        var activePayment = await db.Payments.AsNoTracking()
            .Where(x => x.UserId == userId && x.PlanId == request.PlanId && x.AutoRenew == request.AutoRenew
                && x.PaymentMethod == "sepay"
                && x.Status == "pending" && x.ExpiresAt > now)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (activePayment != null)
            return (activePayment, plan.Name);

        // Sinh mã chuyển khoản duy nhất
        var transactionCode = await GenerateUniqueTransactionCodeAsync(ct);

        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(),
            UserId = userId,
            PlanId = plan.PlanId,
            Amount = plan.Price,
            Currency = "VND",
            PaymentMethod = "sepay",
            TransactionCode = transactionCode,
            Status = "pending",
            IdempotencyKey = key,
            AutoRenew = request.AutoRenew,
            ExpiresAt = now.AddMinutes(options.PaymentExpiryMinutes),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        expiryWakeup?.Signal();

        return (payment, plan.Name);
    }

    public async Task CancelAsync(Guid userId, Guid paymentId, CancellationToken ct = default)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (transaction != null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"plan-membership:" + userId}, 0))", ct);

        var payment = await db.Payments.SingleOrDefaultAsync(x => x.PaymentId == paymentId && x.UserId == userId, ct)
            ?? throw new PaymentException(404, "PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.");
        if (payment.Status == "cancelled")
        {
            if (transaction != null) await transaction.CommitAsync(ct);
            return;
        }
        if (payment.Status != "pending")
            throw new PaymentException(409, "PAYMENT_CANNOT_BE_CANCELLED", "Giao dịch này không còn ở trạng thái chờ thanh toán.");

        payment.Status = "cancelled";
        payment.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
    }

    // ─── HandleSepayWebhookAsync ──────────────────────────────────────────────

    public async Task<bool> HandleSepayWebhookAsync(SepayWebhookPayload payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.SecretKey) || string.IsNullOrWhiteSpace(options.AccountNumber)
            || payload.AccountNumber != options.AccountNumber || payload.Id <= 0)
            return false;
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (transaction != null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"sepay:" + payload.Id}, 0))", ct);
        // Chỉ xử lý tiền vào
        if (!string.Equals(payload.TransferType, "in", StringComparison.OrdinalIgnoreCase))
            return true;

        // Deduplication: nếu đã xử lý giao dịch này rồi (theo SepayTransactionId) thì bỏ qua
        var alreadyProcessed = await db.Payments
            .AnyAsync(x => x.SepayTransactionId == payload.Id && x.Status == "paid", ct);
        if (alreadyProcessed)
            return true;

        // Tìm payment khớp với mã chuyển khoản trong nội dung
        var payment = await FindMatchingPaymentAsync(payload, ct);
        if (payment == null)
            return true; // Giao dịch không liên quan đến HuTube → trả true để SePay không retry
        if (payment.PaymentMethod != "sepay")
            return true;

        if (transaction != null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"plan-membership:" + payment.UserId}, 0))", ct);
            await db.Entry(payment).ReloadAsync(ct);
        }

        var now = Now;

        // Validate số tiền
        if ((decimal)payload.TransferAmount < payment.Amount)
            return true; // Số tiền không đủ — không xử lý nhưng vẫn trả true

        // Validate payment còn pending
        if (payment.Status != "pending")
            return true;

        // Validate chưa hết hạn
        if (payment.ExpiresAt < now)
        {
            payment.Status = "cancelled";
            payment.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return true;
        }

        // Kích hoạt gói
        var planHistoryId = await planService.ActivatePaidPlanAsync(
            payment.UserId, payment.PlanId, payment.PaymentId, autoRenew: payment.AutoRenew, ct);

        // Cập nhật payment
        payment.Status = "paid";
        payment.PaidAt = now;
        payment.UpdatedAt = now;
        payment.SepayTransactionId = payload.Id;
        payment.PlanHistoryId = planHistoryId;
        payment.GatewayPayload = JsonSerializer.Serialize(payload);

        await db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return true;
    }

    // ─── GetUserPaymentsAsync ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<PaymentSummaryResponse>> GetUserPaymentsAsync(Guid userId, CancellationToken ct = default)
    {
        var results = await (
            from p in db.Payments.AsNoTracking()
            join pl in db.Plans.AsNoTracking() on p.PlanId equals pl.PlanId
            where p.UserId == userId
            orderby p.CreatedAt descending
            select new PaymentSummaryResponse(
                p.PaymentId,
                p.PlanId,
                pl.Name,
                p.TransactionCode,
                p.Amount,
                p.Currency,
                p.Status,
                p.PaidAt,
                p.CreatedAt,
                p.ExpiresAt)
        ).ToListAsync(ct);

        return results;
    }

    // ─── GetPaymentAsync ──────────────────────────────────────────────────────

    public async Task<PaymentSummaryResponse?> GetPaymentAsync(Guid userId, Guid paymentId, CancellationToken ct = default)
    {
        return await (
            from p in db.Payments.AsNoTracking()
            join pl in db.Plans.AsNoTracking() on p.PlanId equals pl.PlanId
            where p.UserId == userId && p.PaymentId == paymentId
            select new PaymentSummaryResponse(
                p.PaymentId,
                p.PlanId,
                pl.Name,
                p.TransactionCode,
                p.Amount,
                p.Currency,
                p.Status,
                p.PaidAt,
                p.CreatedAt,
                p.ExpiresAt)
        ).FirstOrDefaultAsync(ct);
    }

    // ─── Private Helpers ──────────────────────────────────────────────────────

    private async Task<Payment?> FindMatchingPaymentAsync(SepayWebhookPayload payload, CancellationToken ct)
    {
        // SePay có thể trích xuất mã tự động vào field "code", hoặc mã nằm trong "content"
        var candidates = new List<string?> { payload.Code };

        // Tìm trong nội dung chuyển khoản (content chứa mã dạng "HUTUBE-XXXXXXXX")
        var content = payload.Content?.ToUpperInvariant() ?? "";
        var prefix = (options.TransactionCodePrefix + "-").ToUpperInvariant();
        var idx = content.IndexOf(prefix, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var rest = content[(idx + prefix.Length)..];
            var end = rest.IndexOfAny([' ', '\t', '\n', '\r']);
            var code = end >= 0 ? rest[..end] : rest;
            if (code.Length >= 6) // mã tối thiểu 6 ký tự
                candidates.Add(options.TransactionCodePrefix + "-" + code);
        }

        // VietQR/banks may remove the separator or replace it with whitespace.
        // Require a complete eight-character reference; never match a prefix of
        // a longer unrelated identifier. Keep the original stored format.
        var referencePattern = @"(?<![A-Z0-9])" + Regex.Escape(options.TransactionCodePrefix.ToUpperInvariant())
            + @"[-\s]*([A-Z0-9]{8})(?![A-Z0-9])";
        foreach (var value in new[] { payload.Code, payload.Content })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            foreach (Match match in Regex.Matches(value.ToUpperInvariant(), referencePattern,
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                candidates.Add(options.TransactionCodePrefix + "-" + match.Groups[1].Value);
        }

        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var payment = await db.Payments
                .FirstOrDefaultAsync(x => x.TransactionCode == candidate, ct);
            if (payment != null) return payment;
        }

        return null;
    }

    private async Task<string> GenerateUniqueTransactionCodeAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var suffix = GenerateRandomCode(8);
            var code = $"{options.TransactionCodePrefix}-{suffix}";
            var exists = await db.Payments.AnyAsync(x => x.TransactionCode == code, ct);
            if (!exists) return code;
        }
        throw new PaymentException(500, "CODE_GENERATION_FAILED", "Không thể tạo mã thanh toán. Vui lòng thử lại.");
    }

    private static string GenerateRandomCode(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // bỏ 0/O, 1/I để dễ đọc
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    private CreatePaymentResponse BuildResponse(Payment payment, string planName)
    {
        var accountNumber = options.AccountNumber.Trim();
        var bankId = NormalizeVietQrBankId(options.BankCode);
        var qrUrl = string.IsNullOrWhiteSpace(accountNumber)
            ? "" // Chưa cấu hình → trả về rỗng; frontend hiển thị thông báo
            : $"https://img.vietqr.io/image/{Uri.EscapeDataString(bankId)}-{Uri.EscapeDataString(accountNumber)}-qr_only.png" +
              $"?amount={payment.Amount.ToString("0", CultureInfo.InvariantCulture)}" +
              $"&addInfo={Uri.EscapeDataString(payment.TransactionCode)}";

        return new CreatePaymentResponse(
            payment.PaymentId,
            payment.PlanId,
            planName,
            payment.TransactionCode,
            qrUrl,
            payment.Amount,
            payment.Currency,
            payment.ExpiresAt);
    }

    private static string NormalizeVietQrBankId(string bankCode)
    {
        var normalized = bankCode.Trim();
        return normalized.Equals("TPBANK", StringComparison.OrdinalIgnoreCase)
            ? "970423" // TPBank BIN; supports the official VietQR Quick Link format.
            : normalized;
    }
}
