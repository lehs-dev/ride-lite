namespace PaymentService.Application.Abstractions;

/// <summary>Intent gửi lên cổng thanh toán (mock MoMo sandbox).</summary>
public record PaymentIntent(
    Guid PaymentId,
    Guid TripId,
    string PaymentRequestKey,
    decimal Amount,
    string Currency,
    PaymentMethodKind Method);

/// <summary>Loại phương thức ở tầng application — domain enum không rò rỉ vào abstraction.</summary>
public enum PaymentMethodKind
{
    MoMoApp,
    MoMoWallet,

    /// <summary>
    /// Thu tiền mặt: NOT a gateway call. <see cref="IPaymentGateway"/> không bao giờ nhận
    /// giá trị này vì PaymentProcessor chốt nghĩa vụ ngay trong domain (P1-1). Thành viên
    /// vẫn tồn tại để switch ánh xạ domain→application là toàn vẹn và compile-check được.
    /// </summary>
    Cash
}

public record GatewayChargeResult(
    bool Success,
    bool Retryable,
    string Code,
    string Message,
    string? PartnerCode = null,
    string? OrderRef = null,
    string? TransId = null,
    string? Checksum = null);

/// <summary>
/// Seam tích hợp cổng thanh toán. Implementation hiện tại là MoMo sandbox giả lập;
/// thay bằng client HTTP thật mà không đụng Domain/Application (DIP/OCP).
/// </summary>
public interface IPaymentGateway
{
    Task<GatewayChargeResult> ChargeAsync(PaymentIntent intent, CancellationToken cancellationToken = default);
}