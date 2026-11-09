using SwiftRide.Contracts.Events;
using TripService.Domain;
using TripService.Domain.Entities;
using Xunit;

namespace TripService.Tests.Messaging;

/// <summary>
/// P1-1: logic application của SetPaymentMethod / MarkDroppedOff. Publisher giả cho phép kiểm
/// tra intent mà không cần SQL Server; hành vi outbox thật của TripEventPublisher có test riêng
/// trong RideCompletedContractTests (EF InMemory, không đụng RowVersion).
/// </summary>
public class TripPaymentMethodAppServiceTests
{
    private static readonly Guid RiderId = Guid.NewGuid();
    private static readonly Guid DriverId = Guid.NewGuid();

    /// <summary>Dựng trip qua đúng máy trạng thái domain (không set Status thủ công).</summary>
    private static Trip NewTrip(TripStatus status, out Guid dropoffEventId)
    {
        var trip = Trip.Create(RiderId, "18 Nguyen Hue", 10.7758m, 106.7019m,
            "35 Le Duan", 10.7834m, 106.6950m, 50_000m);

        dropoffEventId = Guid.Empty;

        switch (status)
        {
            case TripStatus.Requested:
                return trip;
            case TripStatus.Cancelled:
                trip.Cancel(RiderId, actorIsRider: true);
                return trip;
        }

        trip.AssignDriver(DriverId);
        if (status == TripStatus.DriverAccepted) return trip;

        trip.MarkEnRoute(DriverId);
        if (status == TripStatus.EnRoute) return trip;

        trip.MarkPickedUp(DriverId);
        if (status == TripStatus.PickedUp) return trip;

        trip.MarkDroppedOff(DriverId, out dropoffEventId);
        if (status is TripStatus.Paid or TripStatus.Failed)
            trip.TryMarkPaid(Guid.NewGuid(), trip.PaymentRequestKey!, null);

        return trip;
    }

    private static Trip NewTrip(TripStatus status) => NewTrip(status, out _);

    [Fact]
    public async Task SetPaymentMethod_BeforeDropoff_StagesIntentWithChosenMethod()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var publisher = new FakeTripEventPublisher();
        var service = TripAppServiceFactory.Create(publisher, trip);

        await service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet);

        var staged = Assert.Single(publisher.Enqueued);
        Assert.Equal(RidePaymentMethod.MoMoWallet, staged.Method);
        // Key nháp cố ý sai quy ước: nếu lọt lên broker thì consumer từ chối, không tạo nghĩa vụ.
        Assert.NotEqual(EventContracts.BuildPaymentRequestKey(trip.Id), staged.PaymentRequestKey);
    }

    [Fact]
    public async Task SetPaymentMethod_NotTheRider_IsRejected()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var service = TripAppServiceFactory.Create(new FakeTripEventPublisher(), trip);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SetPaymentMethodAsync(trip.Id, Guid.NewGuid(), RidePaymentMethod.MoMoWallet));
    }

    [Fact]
    public async Task SetPaymentMethod_TripAlreadyPaid_IsRejected()
    {
        var trip = NewTrip(TripStatus.Paid);
        var publisher = new FakeTripEventPublisher();
        var service = TripAppServiceFactory.Create(publisher, trip);

        var ex = await Assert.ThrowsAsync<InvalidTripTransitionException>(() =>
            service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet));

        Assert.Contains("không đổi được phương thức thanh toán", ex.Message);
        Assert.Empty(publisher.Enqueued);
    }

    [Fact]
    public async Task SetPaymentMethod_TripCancelled_IsRejected()
    {
        var trip = NewTrip(TripStatus.Cancelled);
        var service = TripAppServiceFactory.Create(new FakeTripEventPublisher(), trip);

        await Assert.ThrowsAsync<InvalidTripTransitionException>(() =>
            service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet));
    }

    [Fact]
    public async Task SetPaymentMethod_TripNotFound_IsKeyNotFound()
    {
        var service = TripAppServiceFactory.Create(new FakeTripEventPublisher());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.SetPaymentMethodAsync(Guid.NewGuid(), RiderId, RidePaymentMethod.MoMoWallet));
    }

    [Fact]
    public async Task SetPaymentMethod_PaymentPendingWithDispatchedIntent_RepublishesNewEventId()
    {
        var trip = NewTrip(TripStatus.PaymentPending, out var originalEventId);
        var publisher = new FakeTripEventPublisher { PendingCount = 0 }; // intent đã lên broker
        var service = TripAppServiceFactory.Create(publisher, trip);

        await service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet);

        var republished = Assert.Single(publisher.Enqueued);
        Assert.NotEqual(originalEventId, republished.EventId);
        Assert.Equal(EventContracts.BuildPaymentRequestKey(trip.Id), republished.PaymentRequestKey);
        Assert.Equal(RidePaymentMethod.MoMoWallet, republished.Method);
    }

    [Fact]
    public async Task SetPaymentMethod_PaymentPendingWithPendingIntent_ReplacesItInPlace()
    {
        var trip = NewTrip(TripStatus.PaymentPending);
        var publisher = new FakeTripEventPublisher { PendingCount = 1 };
        var service = TripAppServiceFactory.Create(publisher, trip);

        await service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet);

        // Bản cũ phải bị xóa trước khi ghi bản mới, nếu không dispatcher gửi cả hai.
        Assert.Single(publisher.DroppedTrips);
        Assert.Equal(RidePaymentMethod.MoMoWallet, Assert.Single(publisher.Enqueued).Method);
    }

    [Fact]
    public async Task SetPaymentMethod_DoesNotChangeTripStatus()
    {
        var trip = NewTrip(TripStatus.PaymentPending);
        var service = TripAppServiceFactory.Create(new FakeTripEventPublisher(), trip);

        var response = await service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet);

        Assert.Equal(nameof(TripStatus.PaymentPending), response.Status);
        Assert.Equal(nameof(RidePaymentMethod.MoMoWallet), response.PaymentMethod);
    }

    [Fact]
    public async Task MarkDroppedOff_MethodFromRequest_GoesIntoEvent()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var publisher = new FakeTripEventPublisher();
        var service = TripAppServiceFactory.Create(publisher, trip);

        await service.MarkDroppedOffAsync(trip.Id, DriverId, RidePaymentMethod.MoMoWallet);

        var intent = Assert.Single(publisher.Enqueued);
        Assert.Equal(RidePaymentMethod.MoMoWallet, intent.Method);
        Assert.Equal(EventContracts.BuildPaymentRequestKey(trip.Id), intent.PaymentRequestKey);
    }

    [Fact]
    public async Task MarkDroppedOff_NothingChosen_PublishesDefaultMoMoApp()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var publisher = new FakeTripEventPublisher();
        var service = TripAppServiceFactory.Create(publisher, trip);

        await service.MarkDroppedOffAsync(trip.Id, DriverId);

        Assert.Equal(RidePaymentMethod.MoMoApp, Assert.Single(publisher.Enqueued).Method);
    }

    [Fact]
    public async Task MarkDroppedOff_ClearsStagedDraftBeforeWritingRealEvent()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var publisher = new FakeTripEventPublisher();
        var service = TripAppServiceFactory.Create(publisher, trip);

        await service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet);
        await service.MarkDroppedOffAsync(trip.Id, DriverId);

        Assert.Contains(trip.Id, publisher.DroppedTrips);
        Assert.Equal(2, publisher.Enqueued.Count); // một nháp + một event thật
        Assert.Equal(RidePaymentMethod.MoMoWallet, publisher.Enqueued[^1].Method);
    }

    [Fact]
    public async Task MarkDroppedOff_WrongDriver_StillRejected()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var publisher = new FakeTripEventPublisher();
        var service = TripAppServiceFactory.Create(publisher, trip);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.MarkDroppedOffAsync(trip.Id, Guid.NewGuid(), RidePaymentMethod.MoMoWallet));

        Assert.Empty(publisher.Enqueued);
    }

    // ---- GET payment-method: read model có kiểm tra tư cách tham gia ----

    [Fact]
    public async Task GetPaymentMethod_Owner_ReturnsStagedMethod()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var publisher = new FakeTripEventPublisher();
        var service = TripAppServiceFactory.Create(publisher, trip);
        await service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet);

        var method = await service.GetPaymentMethodAsync(trip.Id, RiderId, canViewAll: false);

        Assert.Equal(RidePaymentMethod.MoMoWallet, method);
    }

    [Fact]
    public async Task GetPaymentMethod_Driver_CanView()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var publisher = new FakeTripEventPublisher();
        var service = TripAppServiceFactory.Create(publisher, trip);
        await service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet);

        var method = await service.GetPaymentMethodAsync(trip.Id, DriverId, canViewAll: false);

        Assert.Equal(RidePaymentMethod.MoMoWallet, method);
    }

    [Fact]
    public async Task GetPaymentMethod_NotParticipant_Forbidden()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var service = TripAppServiceFactory.Create(new FakeTripEventPublisher(), trip);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetPaymentMethodAsync(trip.Id, Guid.NewGuid(), canViewAll: false));
    }

    [Fact]
    public async Task GetPaymentMethod_Admin_CanViewOthersTrip()
    {
        var trip = NewTrip(TripStatus.PickedUp);
        var publisher = new FakeTripEventPublisher();
        var service = TripAppServiceFactory.Create(publisher, trip);
        await service.SetPaymentMethodAsync(trip.Id, RiderId, RidePaymentMethod.MoMoWallet);

        var method = await service.GetPaymentMethodAsync(trip.Id, Guid.NewGuid(), canViewAll: true);

        Assert.Equal(RidePaymentMethod.MoMoWallet, method);
    }

    [Fact]
    public async Task GetPaymentMethod_UnknownTrip_NotFound()
    {
        var service = TripAppServiceFactory.Create(new FakeTripEventPublisher());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.GetPaymentMethodAsync(Guid.NewGuid(), RiderId, canViewAll: false));
    }
}
