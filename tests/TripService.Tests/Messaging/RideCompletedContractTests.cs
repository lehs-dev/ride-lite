using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SwiftRide.Contracts.Events;
using TripService.Application.Abstractions;
using TripService.Application.Services;
using TripService.Domain;
using TripService.Domain.Entities;
using TripService.Infrastructure.Payment;
using TripService.Infrastructure.Persistence;
using TripService.Infrastructure.Pricing;

namespace TripService.Tests.Messaging;

/// <summary>
/// P1-1: hợp đồng RideCompleted mang PaymentMethod. Serialize bằng đúng options mà
/// TripEventPublisher ghi payload và OutboxDispatcherWorker đọc lại.
/// </summary>
public class RideCompletedContractTests
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static RideCompleted Sample(RidePaymentMethod method) => new(
        EventId: Guid.NewGuid(),
        TripId: Guid.NewGuid(),
        RiderId: Guid.NewGuid(),
        DriverId: Guid.NewGuid(),
        Amount: 50_000m,
        Currency: "VND",
        OccurredAtUtc: new DateTime(2026, 11, 5, 1, 2, 3, DateTimeKind.Utc),
        CorrelationId: null,
        PaymentRequestKey: "trip:key",
        PaymentMethod: method);

    [Fact]
    public void RideCompleted_RoundTrip_KeepsPaymentMethod()
    {
        var original = Sample(RidePaymentMethod.MoMoWallet);

        var restored = JsonSerializer.Deserialize<RideCompleted>(
            JsonSerializer.Serialize(original, Options), Options);

        Assert.NotNull(restored);
        Assert.Equal(RidePaymentMethod.MoMoWallet, restored!.PaymentMethod);
        Assert.Equal(EventContracts.RideCompletedSchemaVersion, restored.SchemaVersion);
    }

    [Fact]
    public void RideCompleted_LegacyPayload_WithoutPaymentMethod_DefaultsToMoMoApp()
    {
        // Payload do schema 1 ghi ra: không có field paymentMethod.
        const string legacy = """
            {"eventId":"11111111-1111-1111-1111-111111111111",
             "tripId":"22222222-2222-2222-2222-222222222222",
             "riderId":"33333333-3333-3333-3333-333333333333",
             "driverId":null,
             "amount":42000,
             "currency":"VND",
             "occurredAtUtc":"2026-11-05T01:02:03Z",
             "correlationId":null,
             "paymentRequestKey":"trip:abc","schemaVersion":1}
            """;

        var restored = JsonSerializer.Deserialize<RideCompleted>(legacy, Options);

        Assert.NotNull(restored);
        Assert.Equal(RidePaymentMethod.MoMoApp, restored!.PaymentMethod);
    }

    [Fact]
    public void RidePaymentMethod_NumericValues_AreStableAcrossServices()
    {
        // PaymentService cast numeric value này sang PaymentMethod — đổi số là đổi hợp đồng.
        Assert.Equal(1, (short)RidePaymentMethod.MoMoApp);
        Assert.Equal(2, (short)RidePaymentMethod.MoMoWallet);
    }

    [Fact]
    public void Enqueue_WritesSelectedMethodIntoOutboxPayload()
    {
        using var db = TripPaymentMethodTestHelpers.NewDb();
        var publisher = new TripService.Infrastructure.Messaging.TripEventPublisher(db);
        var tripId = Guid.NewGuid();

        publisher.EnqueueRideCompleted(tripId, Guid.NewGuid(), Guid.NewGuid(), 10_000m, "VND",
            EventContracts.BuildPaymentRequestKey(tripId), Guid.NewGuid(), null,
            RidePaymentMethod.MoMoWallet);

        db.SaveChanges();
        var row = Assert.Single(db.OutboxMessages.Local);
        var payload = JsonSerializer.Deserialize<RideCompleted>(row.Payload, Options);

        Assert.NotNull(payload);
        Assert.Equal(RidePaymentMethod.MoMoWallet, payload!.PaymentMethod);
        Assert.Equal(EventContracts.RideCompletedSchemaVersion, payload.SchemaVersion);
    }

    [Fact]
    public void DropPending_RemovesOnlyUndeliveredRowsOfThatTrip()
    {
        using var db = TripPaymentMethodTestHelpers.NewDb();
        var publisher = new TripService.Infrastructure.Messaging.TripEventPublisher(db);
        var tripId = Guid.NewGuid();
        var otherTrip = Guid.NewGuid();

        publisher.EnqueueRideCompleted(tripId, Guid.NewGuid(), null, 1_000m, "VND",
            EventContracts.BuildPaymentRequestKey(tripId), Guid.NewGuid(), null, RidePaymentMethod.MoMoApp);
        publisher.EnqueueRideCompleted(otherTrip, Guid.NewGuid(), null, 1_000m, "VND",
            EventContracts.BuildPaymentRequestKey(otherTrip), Guid.NewGuid(), null, RidePaymentMethod.MoMoApp);
        db.SaveChanges();

        // Bản của otherTrip đã được dispatcher gửi → không được bị xóa.
        foreach (var m in db.OutboxMessages.Local.Where(m => m.AggregateId == otherTrip))
            m.DeliveredAtUtc = DateTime.UtcNow;
        db.SaveChanges();

        publisher.DropPendingRideCompleted(tripId);
        db.SaveChanges();

        Assert.Equal(0, publisher.CountPendingRideCompleted(tripId));
        Assert.Contains(db.OutboxMessages.AsNoTracking(), m => m.AggregateId == otherTrip);
    }
}

internal static class TripPaymentMethodTestHelpers
{
    /// <summary>
    /// InMemory chỉ dùng cho các test KHÔNG đụng RowVersion (outbox/publisher). Test nghiệp vụ
    /// trip chạy trên SQL Server container — xem TripPaymentMethodAppServiceTests.
    /// </summary>
    public static TripDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TripDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                .InMemoryEventId.TransactionIgnoredWarning))
            .Options);
}
