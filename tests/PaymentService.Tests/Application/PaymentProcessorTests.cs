using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PaymentEntity = PaymentService.Domain.Entities.Payment;
using PaymentService.Application.Abstractions;
using PaymentService.Domain;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Persistence;
using PaymentService.Infrastructure.Services;
using SwiftRide.Contracts.Events;

namespace PaymentService.Tests.Application;

/// <summary>
/// Test các bất biến của PaymentProcessor — nơi duy nhất được phép gọi gateway.
/// P0-1 (inbox chỉ claim khi chốt) và P0-3 (PaymentFailed phải được phát) sống ở đây.
/// </summary>
public class PaymentProcessorTests
{
    private readonly Mock<IPaymentGateway> _gateway = new();
    private readonly Mock<IPaymentEventPublisher> _publisher = new();
    private readonly Guid _tripId = Guid.NewGuid();
    private readonly Guid _riderId = Guid.NewGuid();

    private string Key => EventContracts.BuildPaymentRequestKey(_tripId);
    private string BusinessKey => $"{_tripId}:{Key}";

    private PaymentProcessor NewProcessor(PaymentDbContext db) =>
        new(db, _gateway.Object, _publisher.Object, NullLogger<PaymentProcessor>.Instance);

    private void SetupSuccess() =>
        _gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFixtures.Success(95_000m));

    private void SetupTransientFailure() =>
        _gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("sandbox không phản hồi"));

    private ChargeRequest Request(decimal amount = 95_000m) => new(
        _tripId, Key, _riderId, null, amount, "VND", PaymentMethod.MoMoApp, null);

    [Fact]
    public async Task ResolveObligation_AloneDoesNotClaimInbox()
    {
        // P0-1: bản cũ ghi inbox row NGAY khi tạo payment, trước khi gọi gateway. Nếu process
        // chết sau đó thì redelivery thấy inbox -> ACK mù -> payment kẹt Created vĩnh viễn.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        await processor.ResolveObligationAsync(Request(), CancellationToken.None);

        Assert.Equal(1, await db.Payments.CountAsync());
        Assert.Empty(await db.ProcessedEvents.ToListAsync());
    }

    [Fact]
    public async Task Settled_ChargesOnceAndClaimsInbox()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);
        SetupSuccess();

        var payment = await processor.ResolveObligationAsync(Request(), CancellationToken.None);
        var outcome = await processor.ChargeAsync(payment, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        Assert.Equal(ChargeOutcome.Settled, outcome);
        Assert.Equal(PaymentStatus.Settled, (await db.Payments.SingleAsync()).Status);
        Assert.Single(await db.ProcessedEvents.ToListAsync());
        _publisher.Verify(p => p.EnqueuePaymentSettled(
            It.IsAny<Guid>(), _tripId, Key, _riderId, 95_000m, "VND",
            It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task TransientGatewayError_LeavesInboxUnclaimedSoRedeliveryResumes()
    {
        // Redelivery sau lỗi tạm thời phải được làm TIẾP, không được ACK.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);
        SetupTransientFailure();

        var payment = await processor.ResolveObligationAsync(Request(), CancellationToken.None);
        var first = await processor.ChargeAsync(payment, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        Assert.Equal(ChargeOutcome.RetryScheduled, first);
        Assert.Equal(PaymentStatus.PendingRetry, (await db.Payments.SingleAsync()).Status);
        Assert.Empty(await db.ProcessedEvents.ToListAsync());

        SetupSuccess();
        var reloaded = await db.Payments.SingleAsync();
        var second = await processor.ChargeAsync(reloaded, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        Assert.Equal(ChargeOutcome.Settled, second);
        Assert.Single(await db.ProcessedEvents.ToListAsync());
        _gateway.Verify(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task NonRetryableFailure_PublishesPaymentFailed()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);
        _gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewayChargeResult(false, false, "INSUFFICIENT_FUNDS", "không đủ số dư"));

        var payment = await processor.ResolveObligationAsync(Request(213_003m), CancellationToken.None);
        var outcome = await processor.ChargeAsync(payment, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        Assert.Equal(ChargeOutcome.Failed, outcome);
        Assert.Equal(PaymentStatus.Failed, (await db.Payments.SingleAsync()).Status);
        _publisher.Verify(p => p.EnqueuePaymentFailed(
            It.IsAny<Guid>(), _tripId, Key, "INSUFFICIENT_FUNDS", false, It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task ExhaustedRetries_FailsWithoutCallingGatewayAndMarksExhausted()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        var payment = PaymentEntity.CreateFromRide(_tripId, Key, _riderId, null,
            137_007m, "VND", PaymentMethod.MoMoApp, null);
        for (var i = 0; i < PaymentPolicy.MaxGatewayAttempts; i++)
            payment.MarkPendingRetry("GATEWAY_UNAVAILABLE", "retry");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var outcome = await processor.ChargeAsync(payment, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        Assert.Equal(ChargeOutcome.Failed, outcome);
        Assert.Equal(PaymentStatus.Failed, (await db.Payments.SingleAsync()).Status);
        _gateway.Verify(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()), Times.Never);
        _publisher.Verify(p => p.EnqueuePaymentFailed(
            It.IsAny<Guid>(), _tripId, Key, It.IsAny<string>(), true, It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task ChargingPayment_IsNotClaimableTwice()
    {
        // Một phiên đang charge thì redelivery phải đứng ngoài — đây là chặn double-charge
        // khi process sống lại / hai đường vào cùng xử lý một nghĩa vụ.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);
        SetupSuccess();

        var payment = await processor.ResolveObligationAsync(Request(), CancellationToken.None);
        payment.MarkCharging();
        await db.SaveChangesAsync();

        var reloaded = await db.Payments.SingleAsync();
        var outcome = await processor.ChargeAsync(reloaded, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        Assert.Equal(ChargeOutcome.InFlightElsewhere, outcome);
        _gateway.Verify(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await db.ProcessedEvents.ToListAsync());
    }

    [Fact]
    public async Task AlreadySettled_IsNotRechargedByRedelivery()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);
        SetupSuccess();

        var payment = await processor.ResolveObligationAsync(Request(), CancellationToken.None);
        await processor.ChargeAsync(payment, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        var reloaded = await db.Payments.SingleAsync();
        var outcome = await processor.ChargeAsync(reloaded, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        Assert.Equal(ChargeOutcome.AlreadyFinal, outcome);
        _gateway.Verify(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConflictingAmountForSameObligation_Throws()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        await processor.ResolveObligationAsync(Request(95_000m), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ResolveObligationAsync(Request(200_000m), CancellationToken.None));
    }

    [Fact]
    public async Task ConflictingCurrencyForSameObligation_Throws()
    {
        // Cùng key khác tiền tệ là xung đột idempotency y như khác amount: nếu ACK im lặng thì
        // TripService tin rằng đã thu 95_000 VND trong khi row PayDB đang giữ USD.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        await processor.ResolveObligationAsync(Request(), CancellationToken.None);

        var conflict = new ChargeRequest(_tripId, Key, _riderId, null,
            95_000m, "USD", PaymentMethod.MoMoApp, null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ResolveObligationAsync(conflict, CancellationToken.None));
        Assert.Contains(PaymentPolicy.ObligationConflictCode, ex.Message);
    }

    [Fact]
    public async Task SameKeyDifferentAmount_ObligationKeepsServerAuthoritativeValues()
    {
        // P1-6: bản tin duplicate nhưng payload mâu thuẫn không được âm thầm sửa giá trị đã chốt.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        var original = await processor.ResolveObligationAsync(Request(95_000m), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ResolveObligationAsync(Request(200_000m), CancellationToken.None));

        var stored = await db.Payments.SingleAsync();
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal(95_000m, stored.Amount);
        Assert.Empty(await db.ProcessedEvents.ToListAsync());
    }

    [Fact]
    public async Task MethodFromEvent_IsRespectedWhenCreatingObligation()
    {
        // P1-1: method của event phải xuống tới row, không bị thay bằng MoMoApp.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        var request = Request() with { Method = PaymentMethod.MoMoWallet };
        var payment = await processor.ResolveObligationAsync(request, CancellationToken.None);

        Assert.Equal(PaymentMethod.MoMoWallet, (await db.Payments.SingleAsync()).Method);
        Assert.Equal(PaymentMethod.MoMoWallet, payment.Method);
    }

    [Fact]
    public async Task ExistingObligation_KeepsChosenMethodWhenEventCarriesAnotherOne()
    {
        // Rider đổi method qua REST trước dropoff; RideCompleted đến sau không được ghi đè lựa
        // chọn đó — method khác biệt chỉ là thông tin, không phải xung đột.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        await processor.ResolveObligationAsync(
            Request() with { Method = PaymentMethod.MoMoWallet }, CancellationToken.None);

        var kept = await processor.ResolveObligationAsync(
            Request() with { Method = PaymentMethod.MoMoApp }, CancellationToken.None);

        Assert.Equal(PaymentMethod.MoMoWallet, kept.Method);
        Assert.Equal(PaymentMethod.MoMoWallet, (await db.Payments.SingleAsync()).Method);
    }

    [Fact]
    public async Task CashObligation_SettlesWithoutCallingGatewayAndClaimsInbox()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        var payment = await processor.ResolveObligationAsync(
            Request() with { Method = PaymentMethod.Cash }, CancellationToken.None);
        var outcome = await processor.ChargeAsync(payment, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        Assert.Equal(ChargeOutcome.CollectedOffline, outcome);
        Assert.Equal(PaymentStatus.Settled, (await db.Payments.SingleAsync()).Status);
        Assert.Single(await db.ProcessedEvents.ToListAsync());
        _gateway.Verify(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()), Times.Never);
        _publisher.Verify(p => p.EnqueuePaymentSettled(
            It.IsAny<Guid>(), _tripId, Key, _riderId, 95_000m, "VND",
            It.IsAny<Guid>(), null, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task CashObligation_RedeliveryDoesNotSettleTwice()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        var payment = await processor.ResolveObligationAsync(
            Request() with { Method = PaymentMethod.Cash }, CancellationToken.None);
        await processor.ChargeAsync(payment, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        var reloaded = await db.Payments.SingleAsync();
        var outcome = await processor.ChargeAsync(reloaded, Guid.NewGuid(), BusinessKey, CancellationToken.None);

        Assert.Equal(ChargeOutcome.AlreadyFinal, outcome);
        _publisher.Verify(p => p.EnqueuePaymentSettled(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<decimal>(),
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task UnsupportedMethodValue_FailsBeforeTouchingDatabase()
    {
        // Payload có method ngoài enum phải thành ArgumentException ở domain, không phải
        // DbUpdateException từ CHECK constraint — message rõ ràng hơn và không để lại row dở dang.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            processor.ResolveObligationAsync(
                Request() with { Method = (PaymentMethod)77 }, CancellationToken.None));

        Assert.Empty(await db.Payments.ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankRequestKeyPayload_IsRejectedAtDomainLayer(string key)
    {
        // Cùng một guard phải chặn cả đường REST/worker, không riêng consumer: lỗi validate
        // là ArgumentException 400/`_error`, không phải DbUpdateException 500. Key hợp lệ về
        // mặt cú pháp nhưng sai quy ước thì do consumer verify trước khi tới đây.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            processor.ResolveObligationAsync(
                Request() with { PaymentRequestKey = key }, CancellationToken.None));

        Assert.Empty(await db.Payments.ToListAsync());
    }

    [Fact]
    public async Task OverlongRequestKey_IsRejectedBeforeVarcharConstraint()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);
        var longKey = new string('k', PaymentPolicy.MaxPaymentRequestKeyLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            processor.ResolveObligationAsync(
                Request() with { PaymentRequestKey = longKey }, CancellationToken.None));

        Assert.Empty(await db.Payments.ToListAsync());
    }

    [Fact]
    public async Task ManualRetry_ReopensFailedPaymentAndChargesAgain()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var processor = NewProcessor(db);

        var payment = PaymentEntity.CreateFromRide(_tripId, Key, _riderId, null,
            213_003m, "VND", PaymentMethod.MoMoApp, null);
        payment.MarkFailed("INSUFFICIENT_FUNDS", "không đủ số dư");
        payment.PrepareForManualRetry();
        Assert.Equal(0, payment.AttemptCount);
        Assert.Equal(PaymentStatus.Created, payment.Status);

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        SetupSuccess();

        var outcome = await processor.ChargeAsync(payment, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(ChargeOutcome.Settled, outcome);
        Assert.Equal(PaymentStatus.Settled, (await db.Payments.SingleAsync()).Status);
    }
}
