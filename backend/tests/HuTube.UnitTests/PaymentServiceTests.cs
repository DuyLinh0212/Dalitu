using HuTube.Application.Payments;
using HuTube.Domain.Payments;
using HuTube.Domain.Videos;
using HuTube.Infrastructure.Payments;
using HuTube.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HuTube.UnitTests;

public sealed class PaymentServiceTests : IDisposable
{
    private readonly HuTubeDbContext _db;
    private readonly PaymentService _service;
    private readonly SepayOptions _options;

    private sealed class MockPlanService : HuTube.Application.Plans.IPlanService
    {
        public Guid ActivatedPaymentId { get; private set; }
        public Guid ActivatedUserId { get; private set; }
        public Guid ActivatedPlanId { get; private set; }
        public bool ActivatePaidPlanAsyncCalled { get; private set; }

        public Task<Guid> ActivatePaidPlanAsync(Guid userId, Guid planId, Guid paymentId, bool autoRenew, CancellationToken ct = default)
        {
            ActivatePaidPlanAsyncCalled = true;
            ActivatedUserId = userId;
            ActivatedPlanId = planId;
            ActivatedPaymentId = paymentId;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<IReadOnlyList<HuTube.Application.Plans.PlanResponse>> GetPlansAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<HuTube.Application.Plans.PlanResponse>> GetAdminPlansAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HuTube.Application.Plans.PlanResponse> CreatePlanAsync(Guid actorUserId, HuTube.Application.Plans.CreatePlanRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HuTube.Application.Plans.PlanResponse> UpdatePlanAsync(Guid actorUserId, Guid planId, HuTube.Application.Plans.UpdatePlanRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ArchivePlanAsync(Guid actorUserId, Guid planId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HuTube.Application.Plans.PlanResponse> GetPlanByIdAsync(Guid planId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HuTube.Application.Plans.PlanShareResponse> GetPlanShareAsync(Guid planId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HuTube.Application.Plans.PlanDetailResponse?> GetMyPlanAsync(Guid userId, CancellationToken ct = default) => Task.FromResult<HuTube.Application.Plans.PlanDetailResponse?>(null);
        public Task<long?> GetEffectiveStorageLimitAsync(Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HuTube.Application.Plans.PlanResponse> SubscribeAsync(Guid userId, Guid planId, HuTube.Application.Plans.PlanSubscriptionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HuTube.Application.Plans.PlanMemberResponse> InviteMemberAsync(Guid ownerUserId, string email, long? allocatedStorage, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HuTube.Application.Plans.PlanMemberResponse> AcceptInvitationAsync(Guid userId, Guid memberId, string token, bool confirmCancelPaidPlan = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveMemberAsync(Guid ownerUserId, Guid memberId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LeaveSharedPlanAsync(Guid memberUserId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HuTube.Application.Plans.PlanMemberResponse> UpdateMemberStorageAsync(Guid ownerUserId, Guid memberId, long? allocatedStorage, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateOwnerStorageAsync(Guid ownerUserId, long? allocatedStorage, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private readonly MockPlanService _mockPlanService = new();

    public PaymentServiceTests()
    {
        var options = new DbContextOptionsBuilder<HuTubeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new HuTubeDbContext(options);

        _options = new SepayOptions
        {
            SecretKey = "test-secret",
            AccountNumber = "1017588888",
            BankCode = "Vietcombank",
            PaymentExpiryMinutes = 30,
            TransactionCodePrefix = "HUTUBE"
        };

        _service = new PaymentService(_db, _mockPlanService, _options, TimeProvider.System);
    }

    [Theory]
    [InlineData("HUTUBEABCDEFG2", true)]
    [InlineData("hutubeabcdefg2", true)]
    [InlineData("Thanh toan HUTUBE ABCDEFG2.", true)]
    [InlineData("HUTUBE-ABCDEFG2", true)]
    [InlineData("HUTUBEABCDEFG2Z", false)]
    [InlineData("OTHERABCDEFG2", false)]
    [InlineData("HUTUBEABCDEFG", false)]
    public async Task Webhook_MatchesCompleteNormalizedQrReference(string content, bool paid)
    {
        var payment = new Payment { PaymentId = Guid.NewGuid(), UserId = Guid.NewGuid(), PlanId = Guid.NewGuid(),
            TransactionCode = "HUTUBE-ABCDEFG2", Status = "pending", Amount = 12345,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) };
        _db.Payments.Add(payment); await _db.SaveChangesAsync();
        var payload = new SepayWebhookPayload(98765, "QC", "2026-10-16", _options.AccountNumber,
            null, null, content, "in", 12345, 0, "QC");
        Assert.True(await _service.HandleSepayWebhookAsync(payload));
        Assert.Equal(paid ? "paid" : "pending", payment.Status);
        Assert.Equal(paid, _mockPlanService.ActivatePaidPlanAsyncCalled);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task InitiateAsync_DefaultExpiry_IsOneDayAndWakesScheduler()
    {
        _options.PaymentExpiryMinutes = new SepayOptions().PaymentExpiryMinutes;
        var plan = new Plan { PlanId = Guid.NewGuid(), Code = "one-day", Name = "One day",
            Price = 99000, DurationDays = 30, Status = "active" };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();
        using var wakeup = new PaymentExpiryWakeup();
        var service = new PaymentService(_db, _mockPlanService, _options, TimeProvider.System, wakeup);
        var before = DateTimeOffset.UtcNow;

        var payment = await service.InitiateAsync(Guid.NewGuid(), new CreatePaymentRequest(plan.PlanId));

        Assert.InRange(payment.ExpiresAt, before.AddDays(1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(await wakeup.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await wakeup.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task InitiateAsync_WhenPaidPlan_CreatesPendingPaymentWithQr()
    {
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(),
            Code = "creator",
            Name = "Creator Plan",
            Price = 99000,
            DurationDays = 30,
            Status = "active"
        };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();

        var userId = Guid.NewGuid();
        var request = new CreatePaymentRequest(plan.PlanId);

        var result = await _service.InitiateAsync(userId, request);

        Assert.NotNull(result);
        Assert.Equal(plan.Price, result.Amount);
        Assert.StartsWith("HUTUBE-", result.TransactionCode);
        Assert.Contains("img.vietqr.io/image/Vietcombank-1017588888-qr_only.png", result.QrCodeUrl);

        var saved = await _db.Payments.FirstOrDefaultAsync(x => x.PaymentId == result.PaymentId);
        Assert.NotNull(saved);
        Assert.Equal("pending", saved.Status);
    }

    [Fact]
    public async Task InitiateAsync_WhenFreePlan_ThrowsBadRequest()
    {
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(),
            Code = "free",
            Name = "Free Plan",
            Price = 0,
            DurationDays = 3650,
            Status = "active"
        };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();

        var userId = Guid.NewGuid();
        var request = new CreatePaymentRequest(plan.PlanId);

        var ex = await Assert.ThrowsAsync<PaymentException>(() => _service.InitiateAsync(userId, request));
        Assert.Equal(400, ex.Status);
        Assert.Equal("FREE_PLAN_NO_PAYMENT", ex.Code);
    }

    [Fact]
    public async Task InitiateAsync_SameUserAndIdempotencyKey_IsIdempotentAndScoped()
    {
        // Arrange
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(),
            Code = "creator-idempotent",
            Name = "Creator Plan",
            Price = 99000,
            DurationDays = 30,
            Status = "active"
        };
        var otherPlan = new Plan
        {
            PlanId = Guid.NewGuid(),
            Code = "pro-idempotent",
            Name = "Pro Plan",
            Price = 199000,
            DurationDays = 30,
            Status = "active"
        };
        _db.Plans.AddRange(plan, otherPlan);
        await _db.SaveChangesAsync();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        const string key = "same-payment-attempt";

        // Act
        var first = await _service.InitiateAsync(userId, new CreatePaymentRequest(plan.PlanId, false, key));
        var replay = await _service.InitiateAsync(userId, new CreatePaymentRequest(plan.PlanId, false, key));
        var otherUserPayment = await _service.InitiateAsync(otherUserId, new CreatePaymentRequest(plan.PlanId, false, key));
        var conflict = await Assert.ThrowsAsync<PaymentException>(() =>
            _service.InitiateAsync(userId, new CreatePaymentRequest(otherPlan.PlanId, false, key)));

        // Assert
        Assert.Equal(first.PaymentId, replay.PaymentId);
        Assert.NotEqual(first.PaymentId, otherUserPayment.PaymentId);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        Assert.Equal(2, await _db.Payments.CountAsync(payment => payment.IdempotencyKey == key));
    }

    [Fact]
    public async Task CancelAsync_IsIdempotentForPendingAndRejectsPaidOrForeignPayment()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var foreignUserId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        var paidId = Guid.NewGuid();
        _db.Payments.AddRange(
            new Payment
            {
                PaymentId = pendingId,
                UserId = userId,
                PlanId = planId,
                Amount = 99000,
                Status = "pending",
                TransactionCode = "HUTUBE-CANCEL01",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
            },
            new Payment
            {
                PaymentId = paidId,
                UserId = userId,
                PlanId = planId,
                Amount = 99000,
                Status = "paid",
                TransactionCode = "HUTUBE-CANCEL02",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
            });
        await _db.SaveChangesAsync();

        // Act
        await _service.CancelAsync(userId, pendingId);
        await _service.CancelAsync(userId, pendingId);
        var paidException = await Assert.ThrowsAsync<PaymentException>(() => _service.CancelAsync(userId, paidId));
        var foreignException = await Assert.ThrowsAsync<PaymentException>(() => _service.CancelAsync(foreignUserId, pendingId));

        // Assert
        Assert.Equal("cancelled", (await _db.Payments.FindAsync(pendingId))!.Status);
        Assert.Equal("PAYMENT_CANNOT_BE_CANCELLED", paidException.Code);
        Assert.Equal("PAYMENT_NOT_FOUND", foreignException.Code);
    }

    [Fact]
    public async Task HandleSepayWebhookAsync_WhenValidPayment_ActivatesPlanAndMarksPaid()
    {
        var userId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var code = "HUTUBE-XYZ12345";

        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(),
            UserId = userId,
            PlanId = planId,
            Amount = 99000,
            Status = "pending",
            TransactionCode = code,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        var payload = new SepayWebhookPayload(
            Id: 10001,
            Gateway: "Vietcombank",
            TransactionDate: "2026-09-20 22:00:00",
            AccountNumber: "1017588888",
            SubAccount: null,
            Code: code,
            Content: $"{code} chuyen tien dang ky goi",
            TransferType: "in",
            TransferAmount: 99000,
            Accumulated: 99000,
            ReferenceCode: "FT123456"
        );

        var processed = await _service.HandleSepayWebhookAsync(payload);

        Assert.True(processed);
        Assert.True(_mockPlanService.ActivatePaidPlanAsyncCalled);
        Assert.Equal(userId, _mockPlanService.ActivatedUserId);
        Assert.Equal(planId, _mockPlanService.ActivatedPlanId);

        var updated = await _db.Payments.FindAsync(payment.PaymentId);
        Assert.NotNull(updated);
        Assert.Equal("paid", updated.Status);
        Assert.NotNull(updated.PaidAt);
        Assert.Equal(10001, updated.SepayTransactionId);
    }

    [Fact]
    public async Task HandleSepayWebhookAsync_WhenDuplicateTransactionId_IgnoresSilently()
    {
        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PlanId = Guid.NewGuid(),
            Amount = 99000,
            Status = "paid",
            SepayTransactionId = 99999,
            TransactionCode = "HUTUBE-ABCDEF12",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        var payload = new SepayWebhookPayload(
            Id: 99999,
            Gateway: "Vietcombank",
            TransactionDate: "2026-09-20 22:00:00",
            AccountNumber: "1017588888",
            SubAccount: null,
            Code: "HUTUBE-ABCDEF12",
            Content: "HUTUBE-ABCDEF12 duplicate",
            TransferType: "in",
            TransferAmount: 99000,
            Accumulated: 99000,
            ReferenceCode: "FT99999"
        );

        var result = await _service.HandleSepayWebhookAsync(payload);

        Assert.True(result);
        Assert.False(_mockPlanService.ActivatePaidPlanAsyncCalled);
    }

    [Fact]
    public async Task HandleSepayWebhookAsync_WhenAmountLessThanRequired_DoesNotActivatePlan()
    {
        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PlanId = Guid.NewGuid(),
            Amount = 99000,
            Status = "pending",
            TransactionCode = "HUTUBE-LESS1234",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        var payload = new SepayWebhookPayload(
            Id: 10002,
            Gateway: "Vietcombank",
            TransactionDate: "2026-09-20 22:00:00",
            AccountNumber: "1017588888",
            SubAccount: null,
            Code: "HUTUBE-LESS1234",
            Content: "HUTUBE-LESS1234 thiếu tiền",
            TransferType: "in",
            TransferAmount: 50000, // Nhỏ hơn 99000
            Accumulated: 50000,
            ReferenceCode: "FT10002"
        );

        var result = await _service.HandleSepayWebhookAsync(payload);

        Assert.True(result);
        Assert.False(_mockPlanService.ActivatePaidPlanAsyncCalled);

        var updated = await _db.Payments.FindAsync(payment.PaymentId);
        Assert.Equal("pending", updated!.Status);
    }

    [Fact]
    public async Task HandleSepayWebhookAsync_WhenPaymentExpired_CancelsPayment()
    {
        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PlanId = Guid.NewGuid(),
            Amount = 99000,
            Status = "pending",
            TransactionCode = "HUTUBE-EXPIRED1",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) // Đã hết hạn
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        var payload = new SepayWebhookPayload(
            Id: 10003,
            Gateway: "Vietcombank",
            TransactionDate: "2026-09-20 22:00:00",
            AccountNumber: "1017588888",
            SubAccount: null,
            Code: "HUTUBE-EXPIRED1",
            Content: "HUTUBE-EXPIRED1 qua han",
            TransferType: "in",
            TransferAmount: 99000,
            Accumulated: 99000,
            ReferenceCode: "FT10003"
        );

        var result = await _service.HandleSepayWebhookAsync(payload);

        Assert.True(result);
        Assert.False(_mockPlanService.ActivatePaidPlanAsyncCalled);

        var updated = await _db.Payments.FindAsync(payment.PaymentId);
        Assert.Equal("cancelled", updated!.Status);
    }

    [Fact]
    public async Task HandleSepayWebhookAsync_WhenAccountCodeOrDirectionDoesNotMatch_LeavesPaymentPending()
    {
        // Arrange
        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PlanId = Guid.NewGuid(),
            Amount = 99000,
            Status = "pending",
            TransactionCode = "HUTUBE-REJECT01",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        var wrongAccount = new SepayWebhookPayload(20001, "Vietcombank", "2026-09-20 22:00:00", "wrong-account", null,
            payment.TransactionCode, payment.TransactionCode, "in", 99000, 99000, "FT20001");
        var outgoing = new SepayWebhookPayload(20002, "Vietcombank", "2026-09-20 22:00:00", _options.AccountNumber, null,
            payment.TransactionCode, payment.TransactionCode, "out", 99000, 99000, "FT20002");
        var unknownCode = new SepayWebhookPayload(20003, "Vietcombank", "2026-09-20 22:00:00", _options.AccountNumber, null,
            "HUTUBE-UNKNOWN", "HUTUBE-UNKNOWN", "in", 99000, 99000, "FT20003");

        // Act
        var wrongAccountResult = await _service.HandleSepayWebhookAsync(wrongAccount);
        var outgoingResult = await _service.HandleSepayWebhookAsync(outgoing);
        var unknownCodeResult = await _service.HandleSepayWebhookAsync(unknownCode);

        // Assert
        Assert.False(wrongAccountResult);
        Assert.True(outgoingResult);
        Assert.True(unknownCodeResult);
        Assert.False(_mockPlanService.ActivatePaidPlanAsyncCalled);
        Assert.Equal("pending", (await _db.Payments.FindAsync(payment.PaymentId))!.Status);
    }

    [Fact]
    public async Task GetPaymentAsync_ReturnsCorrectPaymentDetails()
    {
        var userId = Guid.NewGuid();
        var plan = new Plan
        {
            PlanId = Guid.NewGuid(),
            Code = "pro",
            Name = "Pro Plan",
            Price = 199000,
            Status = "active"
        };
        _db.Plans.Add(plan);

        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(),
            UserId = userId,
            PlanId = plan.PlanId,
            Amount = 199000,
            Status = "pending",
            TransactionCode = "HUTUBE-DETAIL01",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        var result = await _service.GetPaymentAsync(userId, payment.PaymentId);

        Assert.NotNull(result);
        Assert.Equal(payment.PaymentId, result.PaymentId);
        Assert.Equal("Pro Plan", result.PlanName);
        Assert.Equal("HUTUBE-DETAIL01", result.TransactionCode);
        Assert.Equal(199000, result.Amount);
    }
}
