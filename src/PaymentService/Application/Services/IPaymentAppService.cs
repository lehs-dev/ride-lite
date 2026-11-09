using PaymentService.Application.DTOs;
using PaymentService.Domain.Entities;

namespace PaymentService.Application.Services;

public interface IPaymentAppService
{
    /// <summary>
    /// Đẩy tiếp nghĩa vụ đã có (idempotent). Số tiền luôn lấy từ obligation do TripService chốt,
    /// client không tự POST được giá. Rider chỉ thao tác được payment của chính mình.
    /// </summary>
    Task<PaymentResponse> CreatePaymentAsync(
        Guid tripId,
        PaymentMethod method,
        Guid actorId,
        bool isPrivileged,
        CancellationToken cancellationToken = default);

    /// <summary>Thử lại một payment đã Failed với hạn mức retry mới.</summary>
    Task<PaymentResponse> RetryPaymentAsync(
        Guid paymentId,
        Guid actorId,
        bool isPrivileged,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thử lại nghĩa vụ thanh toán theo trip — dùng khi caller chưa biết payment id (rider đang
    /// PaymentPending). Cùng nghiệp vụ với <see cref="RetryPaymentAsync"/> nhưng tra nghĩa vụ
    /// theo (TripId, PaymentRequestKey) đúng quy ước; không có nghĩa vụ → KeyNotFoundException.
    /// </summary>
    Task<PaymentResponse> RetryPaymentByTripAsync(
        Guid tripId,
        Guid actorId,
        bool isPrivileged,
        CancellationToken cancellationToken = default);

    /// <summary>Đổi phương thức thanh toán khi nghĩa vụ còn mở (P1-1).</summary>
    Task<PaymentResponse> UpdateMethodAsync(
        Guid paymentId,
        PaymentMethod method,
        Guid actorId,
        bool isPrivileged,
        CancellationToken cancellationToken = default);

    Task<PaymentResponse?> GetPaymentAsync(Guid id, Guid actorId, bool canViewAll, CancellationToken cancellationToken = default);

    /// <summary>Nghĩa vụ theo trip — rider lấy payment id khi đang PaymentPending mà trip chưa có PaymentId.</summary>
    Task<PaymentResponse?> GetPaymentByTripAsync(
        Guid tripId, Guid actorId, bool canViewAll, CancellationToken cancellationToken = default);

    Task<PagedResult<PaymentResponse>> ListPaymentsAsync(
        Guid? riderId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lọc audit cho admin. from/to phải cùng hệ quy chiếu (UTC hoặc cả hai Unspecified);
    /// 'from' > 'to', status không parse được, page < 1 hay pageSize ngoài [1,100] đều là
    /// ArgumentException -> 400 tại MapPaymentError, không phải 500.
    /// </summary>
    Task<PagedResult<PaymentAuditRow>> QueryForAdminAsync(
        string? status,
        DateTime? from,
        DateTime? to,
        Guid? tripId,
        Guid? riderId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Chi tiết một payment cho admin (đối soát từng giao dịch).
    /// Không kiểm tra sở hữu vì route đã chốt payments.admin + role Admin.
    /// </summary>
    Task<PaymentResponse?> GetPaymentForAdminAsync(Guid paymentId, CancellationToken cancellationToken = default);
}

public record PaymentAuditRow(
    Guid TransactionId,
    Guid TripId,
    Guid RiderId,
    decimal Amount,
    string Method,
    string Status,
    string? FailureCode,
    string? CorrelationId,
    DateTime Timestamp);