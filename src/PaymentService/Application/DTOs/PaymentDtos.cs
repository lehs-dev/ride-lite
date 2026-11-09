using System.Text.Json.Serialization;
using PaymentService.Domain.Entities;

namespace PaymentService.Application.DTOs;

/// <summary>
/// Đẩy tiếp nghĩa vụ đã có cho một trip (idempotent theo payment request key). CHỈ chứa TripId:
/// số tiền do server chốt từ RideCompleted, còn "thử lại một payment cụ thể" là
/// POST /payments/{id}/retry — hai hành vi không chung một endpoint.
/// </summary>
public record CreatePaymentRequest(
    Guid TripId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] PaymentMethod Method = PaymentMethod.MoMoApp);

/// <summary>Đổi phương thức thanh toán khi nghĩa vụ còn mở (Created/PendingRetry/Charging lease hết hạn).</summary>
public record UpdatePaymentMethodRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] PaymentMethod Method);

public record PaymentResponse(
    Guid Id,
    Guid TripId,
    string PaymentRequestKey,
    Guid RiderId,
    Guid? DriverId,
    decimal Amount,
    string Currency,
    string Method,
    string Status,
    int AttemptCount,
    string? GatewayPartnerCode,
    string? GatewayOrderRef,
    string? GatewayTransId,
    string? FailureCode,
    string? FailureMessage,
    string? CorrelationId,
    DateTime CreatedAtUtc,
    DateTime? SettledAtUtc,
    DateTime? FailedAtUtc)
{
    public static PaymentResponse From(Payment payment) => new(
        payment.Id,
        payment.TripId,
        payment.PaymentRequestKey,
        payment.RiderId,
        payment.DriverId,
        payment.Amount,
        payment.Currency,
        payment.Method.ToString(),
        payment.Status.ToString(),
        payment.AttemptCount,
        payment.GatewayPartnerCode,
        payment.GatewayOrderRef,
        payment.GatewayTransId,
        payment.FailureCode,
        payment.FailureMessage,
        payment.CorrelationId,
        payment.CreatedAtUtc,
        payment.SettledAtUtc,
        payment.FailedAtUtc);
}

public record PagedResult<T>(int Page, int PageSize, int TotalCount, IReadOnlyList<T> Items);