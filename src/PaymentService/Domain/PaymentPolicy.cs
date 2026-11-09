namespace PaymentService.Domain;

using PaymentService.Domain.Entities;

/// <summary>Trần số lần gọi gateway cho một nghĩa vụ thanh toán (R07: retry có giới hạn).</summary>
public static class PaymentPolicy
{
    public const int MaxGatewayAttempts = 3;

    /// <summary>Mã lỗi gán cho payment khi lease của một phiên charge quá hạn chưa chốt.</summary>
    public const string ChargingLeaseExpiredCode = "CHARGING_LEASE_EXPIRED";

    /// <summary>
    /// Payload của một RideCompleted đã vào inbox là duy nhất với kết quả đã chốt — mọi bản
    /// tin cùng key mà khác số tiền/tiền tệ/method đều mâu thuẫn và phải rơi vào _error.
    /// </summary>
    public const string ObligationConflictCode = "OBLIGATION_CONFLICT";

    /// <summary>Giới hạn cột PaymentRequestKey trong PayDB (xem PaymentDbContext).</summary>
    public const int MaxPaymentRequestKeyLength = 128;

    /// <summary>ISO-4217: ba ký tự, đúng ràng buộc CHECK/trong DB đã triển khai.</summary>
    public const int CurrencyCodeLength = 3;

    /// <summary>
    /// Event contract chỉ chấp nhận phương thức đã liệt kê trong enum. Bản cũ của RideCompleted
    /// không mang field này → consumer fallback về <see cref="PaymentMethod.MoMoApp"/>.
    /// </summary>
    public static bool IsSupportedMethod(PaymentMethod method) =>
        Enum.IsDefined(method);

    /// <summary>
    /// Tiền mặt được thu ngoài cổng điện tử: nghĩa vụ vẫn ghi sổ nhưng không có gateway call,
    /// nên processor chốt Settled trực tiếp thay vì đánh dấu attempt.
    /// </summary>
    public static bool IsOfflineCollection(PaymentMethod method) => method == PaymentMethod.Cash;

    /// <summary>
    /// Một phiên Charging được coi là vẫn đang chạy trong khoảng này. Quá hạn nghĩa là
    /// process chết giữa lúc gọi gateway và lần ghi kết quả — worker reclaim để resume.
    /// </summary>
    public static readonly TimeSpan ChargingLease = TimeSpan.FromSeconds(90);

    /// <summary>Payment ở Created lâu hơn mức này mà không có phiên nào claim → worker recovery.</summary>
    public static readonly TimeSpan OrphanedCreatedGrace = TimeSpan.FromSeconds(30);
}
