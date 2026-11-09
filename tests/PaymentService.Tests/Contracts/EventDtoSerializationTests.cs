using System.Text.Json;
using SwiftRide.Contracts.Events;

namespace PaymentService.Tests.Contracts;

public class EventDtoSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RideCompleted_RoundTrip_KeepsAllFields()
    {
        var tripId = Guid.NewGuid();
        var original = new RideCompleted(
            EventId: Guid.NewGuid(),
            TripId: tripId,
            RiderId: Guid.NewGuid(),
            DriverId: Guid.NewGuid(),
            Amount: 95_000m,
            Currency: "VND",
            OccurredAtUtc: new DateTime(2026, 11, 2, 3, 4, 5, DateTimeKind.Utc),
            CorrelationId: Guid.NewGuid(),
            PaymentRequestKey: EventContracts.BuildPaymentRequestKey(tripId));

        var restored = JsonSerializer.Deserialize<RideCompleted>(
            JsonSerializer.Serialize(original, Options), Options);

        Assert.NotNull(restored);
        Assert.Equal(original.EventId, restored!.EventId);
        Assert.Equal(original.TripId, restored.TripId);
        Assert.Equal(original.Amount, restored.Amount);
        Assert.Equal(original.Currency, restored.Currency);
        Assert.Equal(original.PaymentRequestKey, restored.PaymentRequestKey);
        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
    }

    [Fact]
    public void PaymentSettled_RoundTrip_KeepsAllFields()
    {
        var original = new PaymentSettled(
            EventId: Guid.NewGuid(),
            TripId: Guid.NewGuid(),
            PaymentId: Guid.NewGuid(),
            TransactionId: Guid.NewGuid(),
            PaymentRequestKey: "trip-key",
            Amount: 137_000m,
            Currency: "VND",
            OccurredAtUtc: DateTime.UtcNow,
            CorrelationId: null);

        var restored = JsonSerializer.Deserialize<PaymentSettled>(
            JsonSerializer.Serialize(original, Options), Options);

        Assert.NotNull(restored);
        Assert.Equal(original.PaymentId, restored!.PaymentId);
        Assert.Equal(original.TransactionId, restored.TransactionId);
        Assert.Null(restored.CorrelationId);
    }

    [Fact]
    public void PaymentFailed_RoundTrip_KeepsAttemptCount()
    {
        var original = new PaymentFailed(
            EventId: Guid.NewGuid(),
            TripId: Guid.NewGuid(),
            PaymentId: Guid.NewGuid(),
            PaymentRequestKey: "trip-key",
            ReasonCode: "GATEWAY_UNAVAILABLE",
            RetryExhausted: true,
            OccurredAtUtc: DateTime.UtcNow,
            CorrelationId: null,
            AttemptCount: 3);

        var restored = JsonSerializer.Deserialize<PaymentFailed>(
            JsonSerializer.Serialize(original, Options), Options);

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.AttemptCount);
        Assert.True(restored.RetryExhausted);
        Assert.Equal("GATEWAY_UNAVAILABLE", restored.ReasonCode);
    }

    [Fact]
    public void PaymentFailed_BackwardCompatible_DefaultsAttemptCountToZero()
    {
        // Payload schema 1 (thiếu AttemptCount) vẫn deserialize được — tránh vỡ hợp đồng khi
        // producer cũ phát mà consumer mới đọc.
        const string legacyJson = """
        {
            "eventId": "11111111-1111-1111-1111-111111111111",
            "tripId": "22222222-2222-2222-2222-222222222222",
            "paymentId": "33333333-3333-3333-3333-333333333333",
            "paymentRequestKey": "trip:22222222222222222222222222222222",
            "reasonCode": "INSUFFICIENT_FUNDS",
            "retryExhausted": false,
            "occurredAtUtc": "2026-11-02T03:04:05Z",
            "correlationId": null,
            "schemaVersion": 1
        }
        """;

        var restored = JsonSerializer.Deserialize<PaymentFailed>(legacyJson, Options);

        Assert.NotNull(restored);
        Assert.Equal(0, restored!.AttemptCount);
    }

    [Fact]
    public void BuildPaymentRequestKey_IsStableAndTripScoped()
    {
        var tripId = Guid.NewGuid();

        Assert.Equal(EventContracts.BuildPaymentRequestKey(tripId), EventContracts.BuildPaymentRequestKey(tripId));
        Assert.DoesNotContain("-", EventContracts.BuildPaymentRequestKey(tripId));
        Assert.NotEqual(EventContracts.BuildPaymentRequestKey(tripId),
            EventContracts.BuildPaymentRequestKey(Guid.NewGuid()));
    }
}
