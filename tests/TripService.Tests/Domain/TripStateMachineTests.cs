using TripService.Domain;
using TripService.Domain.Entities;

namespace TripService.Tests.Domain;

public class TripStateMachineTests
{
    private static Trip NewTrip() => Trip.Create(
        riderId: Guid.NewGuid(),
        pickupAddress: "18 Nguyen Hue",
        pickupLatitude: 10.7758m,
        pickupLongitude: 106.7019m,
        destinationAddress: "35 Le Duan",
        destinationLatitude: 10.7834m,
        destinationLongitude: 106.6950m,
        estimatedPrice: 95_000m);

    [Fact]
    public void Create_StartsRequested()
    {
        var trip = NewTrip();

        Assert.Equal(TripStatus.Requested, trip.Status);
        Assert.Null(trip.PaymentRequestKey);
    }

    [Fact]
    public void HappyFlow_DropoffMintsPaymentKeyAndLandsInPaymentPending()
    {
        var trip = NewTrip();
        var driverId = Guid.NewGuid();

        trip.AssignDriver(driverId);
        trip.MarkEnRoute(driverId);
        trip.MarkPickedUp(driverId);
        trip.MarkDroppedOff(driverId, out var eventId);

        Assert.Equal(TripStatus.PaymentPending, trip.Status);
        Assert.NotNull(trip.PaymentRequestKey);
        Assert.NotEqual(Guid.Empty, eventId);
    }

    [Fact]
    public void DropoffTwice_IsRejectedByStateMachine()
    {
        var trip = NewTrip();
        var driverId = Guid.NewGuid();
        trip.AssignDriver(driverId);
        trip.MarkEnRoute(driverId);
        trip.MarkPickedUp(driverId);
        trip.MarkDroppedOff(driverId, out _);

        Assert.Throws<InvalidTripTransitionException>(() => trip.MarkDroppedOff(driverId, out _));
    }

    [Fact]
    public void DriverOtherThanOwner_CannotAdvance()
    {
        var trip = NewTrip();
        trip.AssignDriver(Guid.NewGuid());

        Assert.Throws<UnauthorizedAccessException>(() => trip.MarkEnRoute(Guid.NewGuid()));
    }

    [Fact]
    public void MarkPaid_FromPaymentPending_SucceedsOnce()
    {
        var trip = NewTrip();
        var driverId = Guid.NewGuid();
        trip.AssignDriver(driverId);
        trip.MarkEnRoute(driverId);
        trip.MarkPickedUp(driverId);
        trip.MarkDroppedOff(driverId, out _);
        var paymentId = Guid.NewGuid();

        var changed = trip.TryMarkPaid(paymentId, trip.PaymentRequestKey!, "txn-1");
        var again = trip.TryMarkPaid(paymentId, trip.PaymentRequestKey!, "txn-1");

        Assert.True(changed);
        Assert.False(again); // idempotent, không hạ cấp
        Assert.Equal(TripStatus.Paid, trip.Status);
        Assert.Equal(paymentId, trip.PaymentId);
    }

    [Fact]
    public void MarkPaid_BeforeDropoff_Throws()
    {
        var trip = NewTrip();

        Assert.Throws<InvalidTripTransitionException>(
            () => trip.TryMarkPaid(Guid.NewGuid(), "trip-key", null));
    }

    [Fact]
    public void MarkPaid_WrongKey_Throws()
    {
        var trip = NewTrip();
        var driverId = Guid.NewGuid();
        trip.AssignDriver(driverId);
        trip.MarkEnRoute(driverId);
        trip.MarkPickedUp(driverId);
        trip.MarkDroppedOff(driverId, out _);

        Assert.Throws<InvalidTripTransitionException>(
            () => trip.TryMarkPaid(Guid.NewGuid(), "key-khac", null));
    }

    [Fact]
    public void MarkPaid_LateEventForCancelledTrip_IsSafeNoOp()
    {
        // P1-4: event đến muộn sau khi trip hủy — không được throw vào _error, chỉ no-op.
        var trip = NewTrip();
        trip.Cancel(trip.RiderId, actorIsRider: true);

        Assert.False(trip.TryMarkPaid(Guid.NewGuid(), "trip-key", null));
        Assert.Equal(TripStatus.Cancelled, trip.Status);
    }

    [Fact]
    public void MarkPaid_ConsumerMode_BeforeDropoff_IsSafeNoOpNotError()
    {
        // P1-2/P1-4: reader thấy trip pre-dropoff trong lúc writer commit dropoff — consumer
        // phải ack im lặng (no-op) thay vì ném event hợp lệ vào _error.
        var trip = NewTrip();
        trip.AssignDriver(Guid.NewGuid());

        Assert.False(trip.TryMarkPaid(Guid.NewGuid(), "trip-key", null, toleratePreDropoff: true));
        Assert.Equal(TripStatus.DriverAccepted, trip.Status);
        Assert.Null(trip.PaidAtUtc);
    }

    [Fact]
    public void MarkPaid_ConsumerMode_KeyMismatchStillThrows()
    {
        // Race thì bỏ qua được, nhưng key sai trên trip đã mint nghĩa vụ là vi phạm hợp đồng.
        var trip = NewTrip();
        var driverId = Guid.NewGuid();
        trip.AssignDriver(driverId);
        trip.MarkEnRoute(driverId);
        trip.MarkPickedUp(driverId);
        trip.MarkDroppedOff(driverId, out _);

        Assert.Throws<InvalidTripTransitionException>(
            () => trip.TryMarkPaid(Guid.NewGuid(), "key-khac", null, toleratePreDropoff: true));
        Assert.Equal(TripStatus.PaymentPending, trip.Status);
    }

    [Fact]
    public void MarkPaid_LateEventForFailedTrip_IsSafeNoOp()
    {
        // TripStatusHistory là entity độc lập, không cần EF: dựng trip Failed bằng cách đi tới
        // PaymentPending rồi hạ thủ công trạng thái (domain không có method hạ cấp).
        var trip = NewPendingTrip();
        SetStatus(trip, TripStatus.Failed);

        Assert.False(trip.TryMarkPaid(Guid.NewGuid(), "trip-key", null, toleratePreDropoff: true));
        Assert.Equal(TripStatus.Failed, trip.Status);
    }

    private static Trip NewPendingTrip()
    {
        var trip = NewTrip();
        var driverId = Guid.NewGuid();
        trip.AssignDriver(driverId);
        trip.MarkEnRoute(driverId);
        trip.MarkPickedUp(driverId);
        trip.MarkDroppedOff(driverId, out _);
        return trip;
    }

    /// <summary>Status chỉ có setter private — test cần dựng trạng thái cuối không đạt được bằng API domain.</summary>
    private static void SetStatus(Trip trip, TripStatus status) =>
        typeof(Trip).GetProperty(nameof(Trip.Status))!.SetValue(trip, status);

    [Fact]
    public void Cancel_AfterPickup_IsForbidden()
    {
        var trip = NewTrip();
        var driverId = Guid.NewGuid();
        trip.AssignDriver(driverId);
        trip.MarkEnRoute(driverId);
        trip.MarkPickedUp(driverId);

        Assert.Throws<InvalidTripTransitionException>(() => trip.Cancel(trip.RiderId, actorIsRider: true));
    }

    [Fact]
    public void Cancel_OtherRider_Forbidden()
    {
        var trip = NewTrip();

        Assert.Throws<UnauthorizedAccessException>(() => trip.Cancel(Guid.NewGuid(), actorIsRider: true));
    }

    [Fact]
    public void Create_NonPositiveFare_Throws()
    {
        Assert.Throws<ArgumentException>(() => Trip.Create(Guid.NewGuid(), "a", 0m, 0m, "b", 0m, 0m, 0m));
    }
}
