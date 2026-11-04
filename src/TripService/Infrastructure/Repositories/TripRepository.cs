using TripService.Domain.Repositories;
using TripService.Domain.Entities;
using TripService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace TripService.Infrastructure.Repositories;
public class TripRepository : ITripRepository
{   private readonly TripDbContext db;
    public TripRepository(TripDbContext dbContext)
    {
        db = dbContext;
    } 

    public async Task AddAsync(
        Trip trip,
        CancellationToken cancellationToken = default)
    {
        await db.Trips.AddAsync(trip, cancellationToken);
    }
    public async Task SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        await db.SaveChangesAsync(cancellationToken);
    }
    public async Task<Trip?> GetByIdAsync(Guid tripId,
        CancellationToken cancellationToken = default)
    {
        return await db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken);
    }
    public async Task<Trip?> GetByIdForUpdateAsync(
        Guid tripId,
        CancellationToken cancellationToken = default)
    {
        return await db.Trips
            .AsTracking()
            .FirstOrDefaultAsync(
                trip => trip.Id == tripId,
                cancellationToken);
    }
}