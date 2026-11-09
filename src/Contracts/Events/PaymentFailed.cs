namespace SwiftRide.Contracts.Events;

/// <summary>
/// PaymentService phát khi một nghĩa vụ thanh toán thất bại vĩnh viễn: gateway trả lỗi
/// không retryable, hoặc đã đụng trần MaxGatewayAttempts. TripService tiêu thụ để ghi dấu
/// "cần xử lý" nhưng GIỮ trip ở PaymentPending — không đánh dấu Paid, không tự hoàn tác
/// việc đã chở khách (docs/TripService-Progress.md §8, M5-08).
/// RetryExhausted phân biệt "hết lượt retry kỹ thuật" với "lỗi từ chối ngay từ đầu".
/// AttemptCount là số lần retry đã dùng (0 với lỗi từ chối ngay lần đầu); có giá trị mặc định
/// nên payload schema 1 (thiếu field) vẫn deserialize được.
/// </summary>
public record PaymentFailed(
    Guid EventId,
    Guid TripId,
    Guid PaymentId,
    string PaymentRequestKey,
    string ReasonCode,
    bool RetryExhausted,
    DateTime OccurredAtUtc,
    Guid? CorrelationId,
    int AttemptCount = 0,
    int SchemaVersion = EventContracts.SchemaVersion);
