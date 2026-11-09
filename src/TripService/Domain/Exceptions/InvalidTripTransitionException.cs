namespace TripService.Domain;

/// <summary>Chuyến đi không được phép chuyển sang trạng thái yêu cầu.</summary>
public sealed class InvalidTripTransitionException : InvalidOperationException
{
    public InvalidTripTransitionException(string message) : base(message)
    {
    }
}
