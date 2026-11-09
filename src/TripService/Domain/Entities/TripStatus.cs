namespace TripService.Domain.Entities;

public enum TripStatus : short
{
    Requested = 1,
    DriverAccepted = 2,
    EnRoute = 3,
    PickedUp = 4,
    DroppedOff = 5,
    PaymentPending = 6,
    Paid = 7,
    Cancelled = 8,
    Failed = 9
}
