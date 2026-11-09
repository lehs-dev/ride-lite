using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.Contracts.Events;
using Testcontainers.MsSql;
using TripService.Domain.Entities;
using TripService.Infrastructure.Messaging;
using TripService.Infrastructure.Persistence;

namespace TripService.Tests.Consumers;

/// <summary>
/// M5-08 trên SQL Server thật: PaymentFailed ghi dấu "cần xử lý" nhưng KHÔNG hạ trip đã Paid
/// và không bao giờ tự đánh dấu Paid. Trip giữ PaymentPending là chủ đích (docs/TripService-Progress §8).
/// </summary>
public class PaymentFailedConsumerTests : IAsyncLifetime
{
    private readonly MsSqlContainer _mssql =
        new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private ServiceProvider _provider = null!;
    private IBusControl _bus = null!;

    public async Task InitializeAsync()
    {
        await _mssql.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TripDbContext>(options => options.UseSqlServer(_mssql.GetConnectionString()));

        services.AddMassTransitTestHarness(x =>
            x.AddConsumer<PaymentFailedConsumer, PaymentFailedConsumerDefinition>());

        _provider = services.BuildServiceProvider(validateScopes: false);
        _bus = _provider.GetRequiredService<IBusControl>();
        await _bus.StartAsync();

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TripDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _bus.StopAsync();
        await _provider.DisposeAsync();
        await _mssql.DisposeAsync();
    }

    [Fact]
    public async Task FailureOnPendingTrip_RecordsReasonAndStaysPaymentPending()
    {
        var trip = await SeedTripAsync();

        await PublishFailure(trip.Id, trip.PaymentRequestKey!, "INSUFFICIENT_FUNDS", retryExhausted: true);

        var stored = await LoadTripAsync(trip.Id);
        Assert.Equal(TripStatus.PaymentPending, stored.Status);
        Assert.Null(stored.PaidAtUtc);

        var history = await LoadHistoriesAsync(trip.Id);
        var failure = Assert.Single(history.Where(h => h.Reason != null && h.Reason.Contains("INSUFFICIENT_FUNDS")));
        Assert.Equal(TripStatus.PaymentPending, failure.FromStatus);
        Assert.Equal(TripStatus.PaymentPending, failure.ToStatus);
        Assert.Equal("Consumer", failure.ActorType);
    }

    [Fact]
    public async Task DuplicateFailure_CreatesOnlyOneHistoryRow()
    {
        var trip = await SeedTripAsync();
        var key = trip.PaymentRequestKey!;

        await PublishFailure(trip.Id, key, "GATEWAY_UNAVAILABLE", retryExhausted: true);
        await PublishFailure(trip.Id, key, "GATEWAY_UNAVAILABLE", retryExhausted: true);

        var history = await LoadHistoriesAsync(trip.Id);
        Assert.Equal(1, history.Count(h => h.Reason != null && h.Reason.Contains("GATEWAY_UNAVAILABLE")));
    }

    [Fact]
    public async Task LateFailureAfterSettlement_DoesNotDowngradePaidTrip()
    {
        // T16: "Failed đến sau Settled không hạ Paid".
        var trip = await SeedTripAsync();
        var key = trip.PaymentRequestKey!;

        await SetPaidAsync(trip);
        await PublishFailure(trip.Id, key, "INSUFFICIENT_FUNDS", retryExhausted: false);

        Assert.Equal(TripStatus.Paid, (await LoadTripAsync(trip.Id)).Status);
    }

    [Fact]
    public async Task UnknownTrip_LeavesNoInboxClaim()
    {
        var unknownTripId = Guid.NewGuid();
        var key = $"trip:{unknownTripId:N}";

        // Trip không tồn tại: consumer throw để redelivery rồi rơi vào _error — tuyệt đối
        // không được claim inbox cho một việc chưa làm xong.
        await PublishFailure(unknownTripId, key, "INSUFFICIENT_FUNDS", retryExhausted: false, waitForInbox: false);
        await Task.Delay(1500);

        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        Assert.False(await db.ProcessedEvents.AnyAsync(e =>
            e.ConsumerName == PaymentFailedConsumer.InboxConsumerName
            && e.BusinessKey == $"{unknownTripId}:{key}"));
    }

    private async Task SetPaidAsync(Trip trip)
    {
        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        var stored = await db.Trips.SingleAsync(t => t.Id == trip.Id);
        stored.TryMarkPaid(Guid.NewGuid(), trip.PaymentRequestKey!, Guid.NewGuid().ToString("N"));
        await db.SaveChangesAsync();
    }

    private async Task<Trip> SeedTripAsync()
    {
        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();

        var trip = Trip.Create(Guid.NewGuid(), "a", 10m, 106m, "b", 10m, 106m, 95_000m);
        var driverId = Guid.NewGuid();
        trip.AssignDriver(driverId);
        trip.MarkEnRoute(driverId);
        trip.MarkPickedUp(driverId);
        trip.MarkDroppedOff(driverId, out _);
        db.Trips.Add(trip);
        await db.SaveChangesAsync();
        return trip;
    }

    private async Task<Trip> LoadTripAsync(Guid id)
    {
        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        return await db.Trips.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private async Task<List<TripStatusHistory>> LoadHistoriesAsync(Guid tripId)
    {
        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        return await db.TripStatusHistories.AsNoTracking().Where(h => h.TripId == tripId).ToListAsync();
    }

    private async Task PublishFailure(Guid tripId, string key, string reasonCode, bool retryExhausted) =>
        await PublishFailure(tripId, key, reasonCode, retryExhausted, waitForInbox: true);

    private async Task PublishFailure(
        Guid tripId,
        string key,
        string reasonCode,
        bool retryExhausted,
        bool waitForInbox)
    {
        var endpoint = await _bus.GetSendEndpoint(
            new Uri($"queue:{PaymentFailedConsumerDefinition.QueueName}"));
        await endpoint.Send(new PaymentFailed(
            EventId: Guid.NewGuid(),
            TripId: tripId,
            PaymentId: Guid.NewGuid(),
            PaymentRequestKey: key,
            ReasonCode: reasonCode,
            RetryExhausted: retryExhausted,
            OccurredAtUtc: DateTime.UtcNow,
            CorrelationId: null));

        // Consumer giữ trip ở PaymentPending nên không thể đợi đổi trạng thái — đợi inbox row.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!cts.IsCancellationRequested)
        {
            using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
            if (await db.ProcessedEvents.AnyAsync(e =>
                    e.ConsumerName == PaymentFailedConsumer.InboxConsumerName
                    && e.BusinessKey == $"{tripId}:{key}", cts.Token))
                return;

            await Task.Delay(100, CancellationToken.None);
        }

        if (waitForInbox)
            Assert.Fail($"PaymentFailed cho trip {tripId} không được xử lý trong 20s (có thể đã rơi vào _error).");
    }
}
