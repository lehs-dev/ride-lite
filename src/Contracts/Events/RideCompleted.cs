namespace SwiftRide.Contracts.Events;

/// <summary>
/// TripService phát khi tài xế trả khách hợp lệ (dropoff), mang số tiền đã chốt
/// phía server để PaymentService thu tiền. EventId ổn định qua các lần republish.
/// SchemaVersion 2: thêm PaymentMethod (hành khách chọn ví/phương thức thu hộ).
/// Field có giá trị mặc định nên payload schema 1 (thiếu field) vẫn deserialize được và
/// tương thích binary với assembly cũ; consumer giữ nguyên hành vi mặc định MoMoApp.
/// </summary>
public record RideCompleted(
    Guid EventId,
    Guid TripId,
    Guid RiderId,
    Guid? DriverId,
    decimal Amount,
    string Currency,
    DateTime OccurredAtUtc,
    Guid? CorrelationId,
    string PaymentRequestKey,
    int SchemaVersion = EventContracts.RideCompletedSchemaVersion,
    RidePaymentMethod PaymentMethod = RidePaymentMethod.MoMoApp);
