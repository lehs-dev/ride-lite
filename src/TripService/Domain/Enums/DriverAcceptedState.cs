using TripService.Domain.Enums;

namespace TripService.Domain.States;

public sealed class DriverAcceptedState : TripState
{
    public override TripStatus Status => TripStatus.DriverAccepted;
}