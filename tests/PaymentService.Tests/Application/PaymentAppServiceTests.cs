using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PaymentService.Application.Abstractions;
using PaymentService.Application.DTOs;
using PaymentService.Domain;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Persistence;
using PaymentService.Infrastructure.Services;
using SwiftRide.Contracts.Events;

namespace PaymentService.Tests.Application;

/// <summary>
/// Test đường REST: quyền sở hữu (P0-5) và quy tắc charge.
/// Charge thật delegate sang PaymentProcessor nên ở đây chỉ nghiệm hành vi ứng dụng.
/// </summary>
public class PaymentAppServiceTests
{
    private readonly Mock<IPaymentGateway> _gateway = new();
    private readonly Mock<IPaymentEventPublisher> _publisher = new();
    private readonly Guid _tripId = Guid.NewGuid();
    private readonly Guid _riderId = Guid.NewGuid();
    private readonly Guid _otherRiderId = Guid.NewGuid();

    private PaymentAppService NewService(PaymentDbContext db)
    {
        var processor = new PaymentProcessor(db, _gateway.Object, _publisher.Object,
            NullLogger<PaymentProcessor>.Instance);
        return new PaymentAppService(db, processor, NullLogger<PaymentAppService>.Instance);
    }

    [Fact]
    public async Task CreatePaymentWithoutObligation_NotFound()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CreatePaymentAsync(Guid.NewGuid(), PaymentMethod.MoMoApp, _riderId, isPrivileged: false));
    }

    [Fact]
    public async Task CreatePayment_Owner_ChargesAndSettles()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Settled);
        // Settled sẵn -> CreatePaymentAsync phải idempotent, không gọi gateway thêm.
        var response = await service.CreatePaymentAsync(_tripId, PaymentMethod.MoMoApp, _riderId, isPrivileged: false);

        Assert.Equal(payment.Id, response.Id);
        Assert.Equal("Settled", response.Status);
        _gateway.Verify(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreatePayment_OtherRider_Forbidden()
    {
        // P0-5: trước đây endpoint này chỉ cần payments.create, nên rider A biết TripId của B
        // là trigger được charge tiền của B.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreatePaymentAsync(_tripId, PaymentMethod.MoMoApp, _otherRiderId, isPrivileged: false));

        _gateway.Verify(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreatePayment_Admin_IsAllowedOnBehalf()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);
        _gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFixtures.Success(95_000m));

        var response = await service.CreatePaymentAsync(_tripId, PaymentMethod.MoMoApp, _otherRiderId, isPrivileged: true);

        Assert.Equal("Settled", response.Status);
    }

    [Fact]
    public async Task RetryPayment_OnOpenPayment_ThrowsConflict()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Created);

        await Assert.ThrowsAsync<InvalidPaymentTransitionException>(() =>
            service.RetryPaymentAsync(payment.Id, _riderId, isPrivileged: false));
    }

    [Fact]
    public async Task RetryPayment_OtherRider_Forbidden()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Failed);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.RetryPaymentAsync(payment.Id, _otherRiderId, isPrivileged: false));
    }

    [Fact]
    public async Task RetryPayment_OnFailed_ResetsAttemptBudgetAndCharges()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Failed);
        _gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFixtures.Success(95_000m));

        var response = await service.RetryPaymentAsync(payment.Id, _riderId, isPrivileged: false);

        Assert.Equal("Settled", response.Status);
        Assert.Equal(0, response.AttemptCount);
    }

    [Fact]
    public async Task RetryPaymentByTrip_NoObligation_NotFound()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.RetryPaymentByTripAsync(Guid.NewGuid(), _riderId, isPrivileged: false));
    }

    [Fact]
    public async Task RetryPaymentByTrip_OnFailed_ResetsAttemptBudgetAndCharges()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Failed);
        _gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFixtures.Success(95_000m));

        // Rider chưa biết payment id — tra nghĩa vụ theo trip đúng như TripService sẽ gọi.
        var response = await service.RetryPaymentByTripAsync(_tripId, _riderId, isPrivileged: false);

        Assert.Equal(payment.Id, response.Id);
        Assert.Equal("Settled", response.Status);
    }

    [Fact]
    public async Task RetryPaymentByTrip_OtherRider_Forbidden()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Failed);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.RetryPaymentByTripAsync(_tripId, _otherRiderId, isPrivileged: false));

        _gateway.Verify(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RetryPaymentByTrip_Admin_IsAllowedOnBehalf()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Failed);
        _gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFixtures.Success(95_000m));

        var response = await service.RetryPaymentByTripAsync(_tripId, _otherRiderId, isPrivileged: true);

        Assert.Equal("Settled", response.Status);
    }

    [Fact]
    public async Task RetryPaymentByTrip_OnOpenPayment_ThrowsConflict()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);

        await Assert.ThrowsAsync<InvalidPaymentTransitionException>(() =>
            service.RetryPaymentByTripAsync(_tripId, _riderId, isPrivileged: false));
    }

    [Fact]
    public async Task GetPayment_OtherRider_Forbidden()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Created);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetPaymentAsync(payment.Id, _otherRiderId, canViewAll: false));
    }

    [Fact]
    public async Task CreatePayment_ChargesWithClientMethod_NotHardcodedDefault()
    {
        // P1-1: method từ request phải tới được gateway; consumer chỉ còn là đường mặc định.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);
        PaymentMethodKind? seen = null;
        _gateway.Setup(g => g.ChargeAsync(It.IsAny<PaymentIntent>(), It.IsAny<CancellationToken>()))
            .Callback<PaymentIntent, CancellationToken>((i, _) => seen = i.Method)
            .ReturnsAsync(TestFixtures.Success(95_000m));

        await service.CreatePaymentAsync(_tripId, PaymentMethod.MoMoWallet, _riderId, isPrivileged: false);

        Assert.Equal(PaymentMethodKind.MoMoWallet, seen);
    }

    [Fact]
    public async Task UpdateMethod_OnOpenPayment_PersistsChoice()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Created);

        var response = await service.UpdateMethodAsync(payment.Id, PaymentMethod.MoMoWallet, _riderId, isPrivileged: false);

        Assert.Equal("MoMoWallet", response.Method);
        Assert.Equal(PaymentMethod.MoMoWallet, (await db.Payments.SingleAsync(p => p.Id == payment.Id)).Method);
    }

    [Fact]
    public async Task UpdateMethod_OnSettledPayment_Conflict()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Settled);

        await Assert.ThrowsAsync<InvalidPaymentTransitionException>(() =>
            service.UpdateMethodAsync(payment.Id, PaymentMethod.MoMoWallet, _riderId, isPrivileged: false));
    }

    [Fact]
    public async Task UpdateMethod_OtherRider_Forbidden()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Created);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateMethodAsync(payment.Id, PaymentMethod.MoMoWallet, _otherRiderId, isPrivileged: false));
    }

    [Fact]
    public async Task GetPaymentByTrip_ReturnsObligationForOwner()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Created);

        var response = await service.GetPaymentByTripAsync(_tripId, _riderId, canViewAll: false);

        Assert.NotNull(response);
        Assert.Equal(payment.Id, response!.Id);
    }

    [Fact]
    public async Task GetPaymentByTrip_OtherRider_Forbidden()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetPaymentByTripAsync(_tripId, _otherRiderId, canViewAll: false));
    }

    [Fact]
    public async Task ListPayments_ReturnsPagedResultWithTotalCount()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);
        var second = Payment.CreateFromRide(Guid.NewGuid(), EventContracts.BuildPaymentRequestKey(Guid.NewGuid()),
            _riderId, null, 50_000m, "VND", PaymentMethod.MoMoApp, null);
        db.Payments.Add(second);
        await db.SaveChangesAsync();

        var page = await service.ListPaymentsAsync(_riderId, page: 1, pageSize: 1);

        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(1, page.Page);
    }

    [Fact]
    public async Task QueryForAdmin_InvalidDateRange_BadRequest()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryForAdminAsync(
            null, DateTime.UtcNow, DateTime.UtcNow.AddDays(-1), null, null, 1, 20));
    }

    [Fact]
    public async Task QueryForAdmin_FiltersByTripAndRider_WithTotalCount()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);
        db.Payments.Add(Payment.CreateFromRide(Guid.NewGuid(), EventContracts.BuildPaymentRequestKey(Guid.NewGuid()),
            _otherRiderId, null, 50_000m, "VND", PaymentMethod.MoMoApp, null));
        await db.SaveChangesAsync();

        var mine = await service.QueryForAdminAsync(null, null, null, _tripId, null, 1, 20);
        Assert.Equal(1, mine.TotalCount);

        var theirs = await service.QueryForAdminAsync(null, null, null, null, _otherRiderId, 1, 20);
        Assert.Equal(1, theirs.TotalCount);
    }

    [Fact]
    public async Task GetPaymentForAdmin_ReturnsAnyPayment()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        var payment = SeedPayment(db, PaymentStatus.Created);

        var response = await service.GetPaymentForAdminAsync(payment.Id);

        Assert.NotNull(response);
        Assert.Equal(payment.Id, response!.Id);
    }

    // ---- P1-6: validate tại tầng ứng dụng để boundary trả đúng 400/403/404/409, không 500 ----

    [Fact]
    public async Task ListPayments_PageBelowOne_BadRequest()
    {
        // Skip((page-1)*pageSize) với page=0 ném InvalidOperationException -> 500 nếu không chặn.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ListPaymentsAsync(_riderId, page: 0, pageSize: 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ListPayments_PageSizeOutOfRange_BadRequest(int pageSize)
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ListPaymentsAsync(_riderId, page: 1, pageSize));
    }

    [Fact]
    public async Task QueryForAdmin_StatusNotInEnum_BadRequest()
    {
        // Enum.TryParse("99") trả true nên nếu chỉ dựa vào TryParse thì bộ lọc rỗng lọt qua 200.
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryForAdminAsync(
            "99", null, null, null, null, 1, 20));

        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryForAdminAsync(
            "NotAStatus", null, null, null, null, 1, 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task QueryForAdmin_PageSizeOutOfRange_BadRequest(int pageSize)
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryForAdminAsync(
            null, null, null, null, null, 1, pageSize));
    }

    [Fact]
    public async Task QueryForAdmin_ReversedDateRangeWithUnspecifiedKind_BadRequest()
    {
        // Giá trị từ query string có Kind=Unspecified; phải so trên cùng một trục thời gian.
        var from = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Unspecified);
        var to = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryForAdminAsync(
            null, from, to, null, null, 1, 20));
    }

    [Fact]
    public async Task QueryForAdmin_MixedDateTimeKinds_CompareOnUtcAxis()
    {
        // 2026-03-01T00:00Z lớn hơn 2026-02-28T23:00 khi quy về UTC — trước đây hai Kind khác nhau
        // so sánh khập khiễng nên cặp này bị coi là hợp lệ.
        var from = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 28, 23, 0, 0, DateTimeKind.Unspecified);

        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryForAdminAsync(
            null, from, to, null, null, 1, 20));
    }

    [Fact]
    public async Task QueryForAdmin_ValidRange_FiltersWithoutThrowing()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);
        SeedPayment(db, PaymentStatus.Created);

        var rows = await service.QueryForAdminAsync(null, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1),
            _tripId, _riderId, 1, 20);

        Assert.Equal(1, rows.TotalCount);
        Assert.Equal(_tripId, rows.Items.Single().TripId);
    }

    [Fact]
    public async Task GetPaymentForAdmin_EmptyId_BadRequest()
    {
        await using var db = new PaymentDbContext(TestFixtures.InMemoryOptions());
        var service = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetPaymentForAdminAsync(Guid.Empty));
    }

    private Payment SeedPayment(PaymentDbContext db, PaymentStatus status)
    {
        var payment = Payment.CreateFromRide(_tripId, EventContracts.BuildPaymentRequestKey(_tripId),
            _riderId, null, 95_000m, "VND", PaymentMethod.MoMoApp, null);

        switch (status)
        {
            case PaymentStatus.Settled:
                payment.MarkAuthorized();
                payment.MarkSettled(Guid.NewGuid());
                break;
            case PaymentStatus.Failed:
                payment.MarkFailed("INSUFFICIENT_FUNDS", "sandbox");
                break;
            case PaymentStatus.Created:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Seed ngoài phạm vi test.");
        }

        db.Payments.Add(payment);
        db.SaveChanges();
        return payment;
    }
}