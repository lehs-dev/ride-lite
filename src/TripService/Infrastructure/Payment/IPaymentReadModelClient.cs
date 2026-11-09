namespace TripService.Infrastructure.Payment;

/// <summary>Snapshot nghĩa vụ thanh toán của một trip, đọc từ PaymentService.</summary>
public sealed record PaymentSnapshot(
    Guid? PaymentId,
    string Status,
    string? FailureCode,
    int AttemptCount);

/// <summary>
/// Read model P1-4: TripService không sở hữu trạng thái payment nhưng rider cần biết
/// nghẽn ở đâu khi trip còn PaymentPending. Failed → "PaymentFailed" (rider retry qua
/// API payment), Settled → "Paid". Best-effort: lỗi mạng = null,
/// trip vẫn hiển thị Status gốc.
/// </summary>
public interface IPaymentReadModelClient
{
    Task<PaymentSnapshot?> TryGetForTripAsync(Guid tripId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rider chủ động thử lại thu tiền cho một trip: gọi POST /payments/{tripId}/retry của
    /// PaymentService (nghĩa vụ lấy theo tripId). TripService KHÔNG tự đổi trạng thái trip —
    /// chỉ Paid do consumer PaymentSettled chuyển tới.
    /// Success = PaymentService chấp nhận và charge lại; khi thất bại FailureReason giải thích
    /// (read model tắt, chưa có nghĩa vụ, 403/409/5xx) và Snapshot có thể vẫn mang hiện trạng.
    /// </summary>
    Task<PaymentRetryOutcome> RetryForTripAsync(Guid tripId, CancellationToken cancellationToken = default);
}

/// <summary>Kết quả retry phía PaymentService. Success = đã nhận yêu cầu và charge lại.</summary>
public sealed record PaymentRetryOutcome(bool Success, PaymentSnapshot? Snapshot, string? FailureReason)
{
    public static PaymentRetryOutcome Failed(string reason) => new(false, null, reason);
}