using TripService.Application.DTOs;
using TripService.Domain.Entities;

namespace TripService.Application.Services;

public interface ITripAppService
{
    Task<TripResponse> CreateTripAsync(Guid riderId, CreateTripRequest request, CancellationToken cancellationToken = default);

    Task<TripResponse> AssignDriverAsync(Guid tripId, Guid driverId, CancellationToken cancellationToken = default);

    Task<TripResponse> MarkEnRouteAsync(Guid tripId, Guid driverId, CancellationToken cancellationToken = default);

    Task<TripResponse> MarkPickedUpAsync(Guid tripId, Guid driverId, CancellationToken cancellationToken = default);

    /// <summary>Trả khách: chốt PaymentPending + ghi intent RideCompleted trong một transaction.
    /// paymentMethod là lựa chọn của hành khách (P1-1), null → mặc định MoMoApp.</summary>
    Task<TripResponse> MarkDroppedOffAsync(Guid tripId, Guid driverId,
        SwiftRide.Contracts.Events.RidePaymentMethod? paymentMethod = null,
        CancellationToken cancellationToken = default);

    Task<TripResponse> CancelTripAsync(Guid tripId, Guid actorId, bool actorIsRider, string? reason,
        CancellationToken cancellationToken = default);

    Task<TripResponse?> GetTripAsync(Guid tripId, Guid actorId, bool canViewAll,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rider đặt/đổi phương thức thanh toán (P1-1). TripDB không có cột riêng cho method nên
    /// lựa chọn sống trong intent RideCompleted của chính trip: trước dropoff thì lần dropoff
    /// sau dùng method mới; sau dropoff thì republish RideCompleted với EventId mới để
    /// PaymentService áp dụng cho nghĩa vụ còn mở.
    /// </summary>
    Task<TripResponse> SetPaymentMethodAsync(Guid tripId, Guid riderId,
        SwiftRide.Contracts.Events.RidePaymentMethod paymentMethod,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phương thức rider đã chọn cho trip (P1-1), suy ra từ intent RideCompleted còn pending
    /// trong outbox. null khi chưa chọn hoặc dispatcher đã gửi hết intent.
    /// Chỉ rider/driver của chuyến hoặc admin (canViewAll) đọc được; trip không tồn tại → 404.
    /// </summary>
    Task<SwiftRide.Contracts.Events.RidePaymentMethod?> GetPaymentMethodAsync(
        Guid tripId, Guid actorId, bool canViewAll, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TripResponse>> ListTripsForAdminAsync(
        string? status, DateTime? from, DateTime? to, int page, int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// P1-2: rider trigger retry thanh toán cho trip của mình. Việc charge thật ủy quyền
    /// hoàn toàn cho PaymentService — TripService không tự đổi TripStatus ở đây. Chỉ chủ nợ
    /// (trip.RiderId) thực hiện được; admin muốn thao tác hộ dùng API của PaymentService.
    /// </summary>
    Task<PaymentRetryResult> RetryPaymentAsync(Guid tripId, Guid actorId, bool actorIsRider,
        CancellationToken cancellationToken = default);
}
