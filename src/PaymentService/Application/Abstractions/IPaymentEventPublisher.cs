namespace PaymentService.Application.Abstractions;

/// <summary>
/// Application layer chỉ enqueue intent, không biết gì về MassTransit/RabbitMQ.
/// Infrastructure ghi row vào bảng outbox trong cùng EF transaction.
/// </summary>
public interface IPaymentEventPublisher
{
    void EnqueuePaymentSettled(
        Guid paymentId,
        Guid tripId,
        string paymentRequestKey,
        Guid riderId,
        decimal amount,
        string currency,
        Guid settledEventId,
        Guid? gatewayTransactionId,
        string? correlationId);

    /// <summary>
    /// Phát khi payment đạt Failed vĩnh viễn (gateway từ chối không retryable, hoặc đã đụng
    /// trần retry). Không có event này thì trip treo PaymentPending mà không ai biết lý do.
    /// </summary>
    void EnqueuePaymentFailed(
        Guid paymentId,
        Guid tripId,
        string paymentRequestKey,
        string reasonCode,
        bool retryExhausted,
        int attemptCount,
        Guid failedEventId,
        string? correlationId);
}
