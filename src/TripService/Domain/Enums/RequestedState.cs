using TripService.Domain.Enums;

namespace TripService.Domain.States;

public sealed class RequestedState : TripState
{
    public override TripStatus Status => TripStatus.Requested;

    public override TripState Accept()
    {
        return new DriverAcceptedState();
    }
}