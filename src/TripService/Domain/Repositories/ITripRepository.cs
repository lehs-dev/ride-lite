using TripService.Domain.Entities;

namespace TripService.Domain.Repositories;

public interface ITripRepository
{
    Task AddAsync(
        Trip trip,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(
        CancellationToken cancellationToken = default);
    Task<Trip?> GetByIdAsync(
        Guid tripId,
        CancellationToken cancellationToken = default);
    Task<Trip?> GetByIdForUpdateAsync(
        Guid tripId,
        CancellationToken cancellationToken = default);
}