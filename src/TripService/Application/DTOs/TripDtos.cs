using TripService.Domain.Entities;

namespace TripService.Application.DTOs;

public record CreateTripRequest(
    string PickupAddress,
    decimal PickupLatitude,
    decimal PickupLongitude,
    string DestinationAddress,
    decimal DestinationLatitude,
    decimal DestinationLongitude);

public record CancelTripRequest(string? Reason = null);

/// <summary>
/// Hành khách chọn/đổi phương thức thanh toán (P1-1). Method là tên enum trung lập trong
/// Contracts (MoMoApp | MoMoWallet) — client không gửi numeric value để tránh khớp nhầm
/// với enum nội bộ của PaymentService.
/// </summary>
public record SetPaymentMethodRequest(string Method);

public record TripResponse(
    Guid Id,
    Guid RiderId,
    Guid? DriverId,
    string Status,
    string PickupAddress,
    decimal PickupLatitude,
    decimal PickupLongitude,
    string DestinationAddress,
    decimal DestinationLatitude,
    decimal DestinationLongitude,
    decimal EstimatedPrice,
    string Currency,
    string? PaymentRequestKey,
    Guid? PaymentId,
    DateTime CreatedAtUtc,
    DateTime? DroppedOffAtUtc,
    DateTime? PaidAtUtc,
    // P1-1: phương thức rider đã chọn cho trip (null = chưa có intent pending để suy ra).
    string? PaymentMethod = null,
    // Read model P1-4: snapshot từ PaymentService, null khi chưa có nghĩa vụ hoặc read model tắt.
    string? PaymentStatus = null,
    string? PaymentFailureCode = null,
    int? PaymentRetryCount = null)
{
    public static TripResponse From(Trip trip) => new(
        trip.Id,
        trip.RiderId,
        trip.DriverId,
        trip.Status.ToString(),
        trip.PickupAddress,
        trip.PickupLatitude,
        trip.PickupLongitude,
        trip.DestinationAddress,
        trip.DestinationLatitude,
        trip.DestinationLongitude,
        trip.EstimatedPrice,
        trip.Currency,
        trip.PaymentRequestKey,
        trip.PaymentId,
        trip.CreatedAtUtc,
        trip.DroppedOffAtUtc,
        trip.PaidAtUtc);

    /// <summary>
    /// Gắn snapshot thanh toán vào response. Bình thường nhãn lấy từ PaymentService; khi read
    /// model không đọc được (tắt, lỗi mạng, 403) nhưng TripDB đã chốt Paid thì vẫn suy ra
    /// "Paid" — trip đã trả tiền không được hiện như thiếu thông tin thanh toán.
    /// </summary>
    public TripResponse WithPaymentSnapshot(PaymentSnapshotDto? snapshot) => snapshot is null
        ? Status == nameof(TripStatus.Paid) ? AsPaidSettled() : this
        : this with
        {
            PaymentId = snapshot.PaymentId ?? PaymentId,
            PaymentStatus = snapshot.Status,
            PaymentFailureCode = snapshot.FailureCode,
            PaymentRetryCount = snapshot.RetryCount
        };

    /// <summary>
    /// Trip đã Paid: tự suy trạng thái thanh toán từ TripStatus để rider/admin luôn thấy
    /// một nhãn nhất quán, kể cả khi read model PaymentService tắt hoặc lỗi mạng.
    /// Failure code/retry count vẫn để trống — trip đã chốt thì chúng không còn ý nghĩa.
    /// </summary>
    public TripResponse AsPaidSettled() => this with { PaymentStatus = "Paid" };
}

/// <summary>DTO trung gian giữa app service và read model client (không rò rỉ type infrastructure).</summary>
public sealed record PaymentSnapshotDto(Guid? PaymentId, string Status, string? FailureCode, int RetryCount);

/// <summary>
/// Kết quả rider bấm retry thanh toán (P1-2). Trip không bao giờ tự đổi trạng thái ở đây —
/// chỉ consumer PaymentSettled mới chuyển sang Paid.
/// </summary>
public enum PaymentRetryStatus
{
    /// <summary>PaymentService đã nhận yêu cầu và đang charge lại.</summary>
    Retrying,

    /// <summary>Yêu cầu hợp lệ nhưng nghĩa vụ chưa tới mức cần người can thiệp
    /// (vd worker vẫn đang retry tự động): rider chỉ cần chờ, trip vẫn PaymentPending.</summary>
    AcceptedNoAction,

    /// <summary>Không thể retry: trip sai trạng thái, người gọi không phải chủ chuyến,
    /// hoặc PaymentService từ chối.</summary>
    Rejected
}

/// <summary>Message luôn giải thích outcome; khi Rejected nó là lý do hiển thị cho rider.</summary>
public sealed record PaymentRetryResult(PaymentRetryStatus Status, TripResponse Trip, string? Message)
{
    public bool Success => Status == PaymentRetryStatus.Retrying;
}
