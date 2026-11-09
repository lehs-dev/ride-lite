namespace TripService.Domain.Services;

/// <summary>Cước do server chốt — client không được tự POST giá (docs/TripService-Progress.md §9.2.1).</summary>
public interface IFareCalculator
{
    decimal Calculate(decimal pickupLatitude, decimal pickupLongitude,
        decimal destinationLatitude, decimal destinationLongitude);
}
