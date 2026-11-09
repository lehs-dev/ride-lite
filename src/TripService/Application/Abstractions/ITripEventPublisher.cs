using SwiftRide.Contracts.Events;

namespace TripService.Application.Abstractions;

/// <summary>
/// Application chỉ enqueue intent, không biết MassTransit (M2-07). Implementation ghi
/// row vào bảng outbox trong chính EF transaction đang mở của caller.
/// </summary>
public interface ITripEventPublisher
{
    void EnqueueRideCompleted(
        Guid tripId,
        Guid riderId,
        Guid? driverId,
        decimal amount,
        string currency,
        string paymentRequestKey,
        Guid eventId,
        string? correlationId,
        RidePaymentMethod paymentMethod = RidePaymentMethod.MoMoApp);

    /// <summary>Số intent RideCompleted của trip chưa được dispatcher gửi lên broker.</summary>
    int CountPendingRideCompleted(Guid tripId);

    /// <summary>
    /// Xóa toàn bộ intent RideCompleted chưa gửi của trip — dùng khi thay method trên bản nháp
    /// hoặc khi dropoff mint event thật đè lên bản nháp rider đã staged.
    /// </summary>
    void DropPendingRideCompleted(Guid tripId);

    /// <summary>
    /// Method ghi trong intent RideCompleted chưa gửi gần nhất của trip; null khi không còn
    /// intent nào. TripDB không có cột riêng cho method nên outbox là nguồn duy nhất.
    /// </summary>
    RidePaymentMethod? PeekPendingPaymentMethod(Guid tripId);
}
