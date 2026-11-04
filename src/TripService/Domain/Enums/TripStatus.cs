namespace TripService.Domain.Enums;

public enum TripStatus
{
    Requested,
    DriverAccepted,
    EnRoute,
    PickedUp,
    DroppedOff,
    PaymentPending,
    Paid,
    Cancelled,
    Failed
}