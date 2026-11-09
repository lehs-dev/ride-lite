namespace SwiftRide.Contracts.Events;

/// <summary>
/// Hợp đồng event giữa TripService và PaymentService qua RabbitMQ.
/// Field list theo docs/TripService-Progress.md §9; idempotency key là
/// (TripId, PaymentRequestKey) chứ không phải EventId (docs/EventContract.md §3.4).
/// </summary>
public static class EventContracts
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Phiên bản của riêng RideCompleted (thêm PaymentMethod). Các event khác vẫn dùng
    /// SchemaVersion = 1; không bump hằng số chung để tránh làm giả version của chúng.
    /// </summary>
    public const int RideCompletedSchemaVersion = 2;

    /// <summary>Tạo payment request key ổn định cho một nghĩa vụ thanh toán của trip.</summary>
    public static string BuildPaymentRequestKey(Guid tripId) => $"trip:{tripId:N}";
}
