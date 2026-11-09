namespace PaymentService.Domain.Entities;

public enum PaymentStatus : short
{
    Created = 1,
    PendingRetry = 2,
    Authorized = 3,
    Settled = 4,
    Failed = 5,

    /// <summary>
    /// Một phiên charge đang chạy: ai đó đã claim nghĩa vụ và đang gọi gateway.
    /// Không phải trạng thái chốt — crash ở đây được worker reclaim sau ChargingLease.
    /// </summary>
    Charging = 8
}
