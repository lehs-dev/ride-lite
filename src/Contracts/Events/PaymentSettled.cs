namespace SwiftRide.Contracts.Events;

/// <summary>
/// PaymentService phát khi giao dịch đã thu tiền thành công. TripService tiêu thụ
/// để chuyển trip từ PaymentPending sang Paid (không có API public cho bước này).
/// </summary>
public record PaymentSettled(
    Guid EventId,
    Guid TripId,
    Guid PaymentId,
    Guid? TransactionId,
    string PaymentRequestKey,
    decimal Amount,
    string Currency,
    DateTime OccurredAtUtc,
    Guid? CorrelationId,
    int SchemaVersion = EventContracts.SchemaVersion);
