using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaymentService.Application.Abstractions;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Persistence;
using SwiftRide.Contracts.Events;

namespace PaymentService.Infrastructure.Messaging;

/// <summary>
/// Ghi intent PaymentSettled vào bảng outbox trong chính transaction đang mở của
/// caller — cam kết "thay đổi trạng thái + ý định phát event" là nguyên tử (M5-04/M5-05).
/// </summary>
public sealed class PaymentEventPublisher : IPaymentEventPublisher
{
    private readonly PaymentDbContext _db;

    public PaymentEventPublisher(PaymentDbContext db)
    {
        _db = db;
    }

    public void EnqueuePaymentSettled(
        Guid paymentId,
        Guid tripId,
        string paymentRequestKey,
        Guid riderId,
        decimal amount,
        string currency,
        Guid settledEventId,
        Guid? gatewayTransactionId,
        string? correlationId)
    {
        var message = new PaymentSettled(
            EventId: settledEventId,
            TripId: tripId,
            PaymentId: paymentId,
            TransactionId: gatewayTransactionId,
            PaymentRequestKey: paymentRequestKey,
            Amount: amount,
            Currency: currency,
            OccurredAtUtc: DateTime.UtcNow,
            CorrelationId: ParseCorrelation(correlationId));

        _db.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = settledEventId,
            EventType = nameof(PaymentSettled),
            AggregateId = paymentId,
            Payload = JsonSerializer.Serialize(message, typeof(PaymentSettled))
        });
    }

    public void EnqueuePaymentFailed(
        Guid paymentId,
        Guid tripId,
        string paymentRequestKey,
        string reasonCode,
        bool retryExhausted,
        int attemptCount,
        Guid failedEventId,
        string? correlationId)
    {
        var message = new PaymentFailed(
            EventId: failedEventId,
            TripId: tripId,
            PaymentId: paymentId,
            PaymentRequestKey: paymentRequestKey,
            ReasonCode: reasonCode,
            RetryExhausted: retryExhausted,
            OccurredAtUtc: DateTime.UtcNow,
            CorrelationId: ParseCorrelation(correlationId),
            AttemptCount: attemptCount);

        _db.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = failedEventId,
            EventType = nameof(PaymentFailed),
            AggregateId = paymentId,
            Payload = JsonSerializer.Serialize(message, typeof(PaymentFailed))
        });
    }

    private static Guid? ParseCorrelation(string? correlationId) =>
        Guid.TryParse(correlationId, out var parsed) ? parsed : null;
}
