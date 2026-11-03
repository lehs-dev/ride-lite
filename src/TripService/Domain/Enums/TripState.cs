using TripService.Domain.Enums;

namespace TripService.Domain.States;

public abstract class TripState
{
    public abstract TripStatus Status { get; }

    public virtual TripState Accept()
    {
        throw new InvalidOperationException(
            $"Không thể nhận chuyến ở trạng thái {Status}.");
    }
}