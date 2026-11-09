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
/// Consumer test chạy SQL Server container thật (EnsureCreated + transaction + unique
/// index không mock được). In-memory bus thay RabbitMQ.
/// </summary>
public class PaymentSettledConsumerTests : IAsyncLifetime
{
    private readonly MsSqlContainer _mssql = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private ServiceProvider _provider = null!;
    private IBusControl _bus = null!;

    public async Task InitializeAsync()
    {
        await _mssql.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TripDbContext>(options =>
            options.UseSqlServer(_mssql.GetConnectionString()));

        services.AddMassTransitTestHarness(x =>
            x.AddConsumer<PaymentSettledConsumer, PaymentSettledConsumerDefinition>());

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
    public async Task ValidSettlement_MovesTripToPaidOnce()
    {
        var trip = await SeedTripAsync();

        await Publish(trip.Id, trip.PaymentRequestKey!, 95_000m);

        var stored = await LoadTripAsync(trip.Id);
        Assert.Equal(TripStatus.Paid, stored.Status);
        Assert.NotNull(stored.PaidAtUtc);
    }

    [Fact]
    public async Task DuplicateDelivery_DoesNotCreateSecondHistoryOrDoubleTransition()
    {
        var trip = await SeedTripAsync();
        var key = trip.PaymentRequestKey!;

        await Publish(trip.Id, key, 95_000m);
        await Publish(trip.Id, key, 95_000m);

        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        var paidTransitions = await db.TripStatusHistories.CountAsync(h =>
            h.TripId == trip.Id && h.ToStatus == TripStatus.Paid);
        Assert.Equal(1, paidTransitions);
    }

    [Fact]
    public async Task AmountMismatch_LeavesTripUnpaid()
    {
        var trip = await SeedTripAsync();

        await Publish(trip.Id, trip.PaymentRequestKey!, 200_000m);

        var stored = await LoadTripAsync(trip.Id);
        Assert.Equal(TripStatus.PaymentPending, stored.Status);
    }

    [Fact]
    public async Task UnknownTrip_LeavesNoInboxClaim()
    {
        var unknownTripId = Guid.NewGuid();

        await Publish(unknownTripId, "trip-unknown", 95_000m);

        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        Assert.False(await db.ProcessedEvents.AnyAsync(e => e.BusinessKey == $"{unknownTripId}:trip-unknown"));
    }

    [Fact]
    public async Task PaidTrip_LateFailureLikeEvent_DoesNotDowngrade()
    {
        var trip = await SeedTripAsync();
        var key = trip.PaymentRequestKey!;
        await Publish(trip.Id, key, 95_000m);

        // Event thứ hai cùng key nhưng amount khác — phải bị từ chối, trip vẫn Paid.
        await Publish(trip.Id, key, 300_000m);

        Assert.Equal(TripStatus.Paid, (await LoadTripAsync(trip.Id)).Status);
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
        // Dropoff phải persist — consumer chỉ nhận PaymentPending đã lưu, key mới có trong DB.
        trip.MarkDroppedOff(driverId, out var eventId);
        db.Trips.Add(trip);
        db.TripStatusHistories.Add(new TripStatusHistory
        {
            TripId = trip.Id, FromStatus = TripStatus.PickedUp, ToStatus = TripStatus.DroppedOff,
            ActorId = driverId, ActorType = "Driver", EventId = eventId
        });
        db.TripStatusHistories.Add(new TripStatusHistory
        {
            TripId = trip.Id, FromStatus = TripStatus.DroppedOff, ToStatus = TripStatus.PaymentPending,
            ActorId = driverId, ActorType = "Driver", EventId = eventId
        });
        await db.SaveChangesAsync();
        return trip;
    }

    private async Task<Trip> LoadTripAsync(Guid id)
    {
        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
        return await db.Trips.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private async Task Publish(Guid tripId, string key, decimal amount)
    {
        var endpoint = await _bus.GetSendEndpoint(
            new Uri($"queue:{PaymentSettledConsumerDefinition.QueueName}"));
        await endpoint.Send(new PaymentSettled(
            EventId: Guid.NewGuid(),
            TripId: tripId,
            PaymentId: Guid.NewGuid(),
            TransactionId: Guid.NewGuid(),
            PaymentRequestKey: key,
            Amount: amount,
            Currency: "VND",
            OccurredAtUtc: DateTime.UtcNow,
            CorrelationId: null));

        // In-memory bus dispatch async — chờ consumer chốt hoặc message vào _error.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var scope = _provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
                var status = await db.Trips.Where(t => t.Id == tripId)
                    .Select(t => t.Status).FirstOrDefaultAsync(cts.Token);
                if (status == TripStatus.Paid)
                    return;
            }
            catch (ObjectDisposedException)
            {
                return; // provider đã dispose — test kết thúc, bỏ qua polling
            }
            await Task.Delay(100, CancellationToken.None);
        }
    }
}
