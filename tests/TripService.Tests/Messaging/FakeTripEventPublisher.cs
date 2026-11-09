using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SwiftRide.Contracts.Events;
using TripService.Application.Abstractions;
using TripService.Application.Services;
using TripService.Domain.Entities;
using TripService.Infrastructure.Payment;
using TripService.Infrastructure.Persistence;
using TripService.Infrastructure.Pricing;
using Xunit;

namespace TripService.Tests.Messaging;

/// <summary>
/// Publisher giả: ghi lại các lời gọi Enqueue và mô phỏng trạng thái outbox
/// (pending = chưa gửi, dispatched = đã lên broker) mà không cần DB thật.
/// </summary>
internal sealed class FakeTripEventPublisher : ITripEventPublisher
{
    internal sealed record Intent(Guid TripId, Guid EventId, string PaymentRequestKey, RidePaymentMethod Method);

    internal List<Intent> Enqueued { get; } = [];

    internal List<Guid> DroppedTrips { get; } = [];

    /// <summary>Số intent RideCompleted còn chờ trong outbox — test tự đặt để mô phỏng dispatched.</summary>
    internal int PendingCount { get; set; }

    /// <summary>Method đọc được từ intent pending gần nhất (null = không có) — mô phỏng payload outbox.</summary>
    internal RidePaymentMethod? StoredMethod { get; set; }

    public void EnqueueRideCompleted(
        Guid tripId, Guid riderId, Guid? driverId, decimal amount, string currency,
        string paymentRequestKey, Guid eventId, string? correlationId,
        RidePaymentMethod paymentMethod = RidePaymentMethod.MoMoApp)
    {
        Enqueued.Add(new Intent(tripId, eventId, paymentRequestKey, paymentMethod));
        PendingCount++;
        StoredMethod = paymentMethod;
    }

    public int CountPendingRideCompleted(Guid tripId) => PendingCount;

    public void DropPendingRideCompleted(Guid tripId)
    {
        DroppedTrips.Add(tripId);
        PendingCount = 0;
        StoredMethod = null;
    }

    public RidePaymentMethod? PeekPendingPaymentMethod(Guid tripId) =>
        PendingCount > 0 ? StoredMethod : null;
}

/// <summary>Read model tắt (hành vi best-effort trả null) — test P1-1 không quan tâm snapshot.</summary>
internal sealed class NoopPaymentReadModelClient : IPaymentReadModelClient
{
    public Task<PaymentSnapshot?> TryGetForTripAsync(Guid tripId, CancellationToken cancellationToken = default)
        => Task.FromResult<PaymentSnapshot?>(null);

    public Task<PaymentRetryOutcome> RetryForTripAsync(Guid tripId, CancellationToken cancellationToken = default)
        => Task.FromResult(PaymentRetryOutcome.Failed("read model tắt trong test"));
}

/// <summary>Dựng TripAppService trên EF InMemory với publisher do test cung cấp.</summary>
internal static class TripAppServiceFactory
{
    public static TripAppService Create(ITripEventPublisher publisher, params Trip[] trips)
    {
        var options = new DbContextOptionsBuilder<TripDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                .InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var db = new TripDbContext(options);
        db.Trips.AddRange(trips);
        db.SaveChanges();

        return new TripAppService(db, new HaversineFareCalculator(Options.Create(new TripPricingOptions())),
            publisher, new NoopPaymentReadModelClient(), Mock.Of<ILogger<TripAppService>>());
    }
}
