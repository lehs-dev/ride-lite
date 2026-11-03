using TripService.Application.DTOs;

namespace TripService.Application.Services;

public interface ITripService
{
    Task<Guid> CreateAsync(
        Guid riderId,
        CreateTripRequest request,
        CancellationToken cancellationToken = default);
    Task<TripResponse?> GetByIdAsync(
        Guid tripId,
        Guid riderId,
        CancellationToken cancellationToken = default);
}