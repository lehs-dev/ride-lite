using System.ComponentModel.DataAnnotations;
using TripService.Application.DTOs;
using TripService.Domain.Entities;
using TripService.Domain.Repositories;

namespace TripService.Application.Services;

public class TripApplicationService : ITripService
{
    private readonly ITripRepository _tripRepository;

    public TripApplicationService(ITripRepository tripRepository)
    {
        _tripRepository = tripRepository;
    }

    public async Task<Guid> CreateAsync(
        Guid riderId,
        CreateTripRequest request,
        CancellationToken cancellationToken = default)
    {
        Validator.ValidateObject(
            request,
            new ValidationContext(request),
            validateAllProperties: true);

        if (riderId == Guid.Empty)
        {
            throw new ArgumentException(
                "Mã hành khách không hợp lệ.",
                nameof(riderId));
        }

        var trip = new Trip
        {
            RiderId = riderId,
            FromLat = request.FromLat!.Value,
            FromLon = request.FromLon!.Value,
            FromAddress = request.FromAddress.Trim(),
            ToLat = request.ToLat!.Value,
            ToLon = request.ToLon!.Value,
            ToAddress = request.ToAddress.Trim(),
            PaymentMethod = request.PaymentMethod.Trim()
        };

        await _tripRepository.AddAsync(trip, cancellationToken);
        await _tripRepository.SaveChangesAsync(cancellationToken);

        return trip.Id;
    }
    public async Task<TripResponse?> GetByIdAsync(
        Guid tripId,
        Guid riderId,
        CancellationToken cancellationToken = default)
    {
        var trip = await _tripRepository.GetByIdAsync(
            tripId,
            cancellationToken);

        if (trip is null || trip.RiderId != riderId)
        {
            return null;
        }

        return new TripResponse
        {
            Id = trip.Id,
            RiderId = trip.RiderId,
            DriverId = trip.DriverId,
            Status = trip.Status.ToString(),

            FromLat = trip.FromLat,
            FromLon = trip.FromLon,
            FromAddress = trip.FromAddress,

            ToLat = trip.ToLat,
            ToLon = trip.ToLon,
            ToAddress = trip.ToAddress,

            Distance = trip.Distance,
            EstimatedTime = trip.EstimatedTime,
            Price = trip.Price,

            PaymentMethod = trip.PaymentMethod,
            PaymentStatus = trip.PaymentStatus.ToString(),

            CreatedAtUtc = trip.CreatedAtUtc,
            UpdatedAtUtc = trip.UpdatedAtUtc,
            PickUpTimeUtc = trip.PickUpTimeUtc,
            DropOffTimeUtc = trip.DropOffTimeUtc
        };
    }
}