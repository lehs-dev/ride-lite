namespace SwiftRide.Contracts.Events;

/// <summary>
/// Phương thức thanh toán mà hành khách chọn cho nghĩa vụ của trip.
/// Kiểu trung lập trong Contracts (không tham chiếu enum domain của PaymentService);
/// numeric value khớp 1-1 với PaymentService.Domain.Entities.PaymentMethod
/// (MoMoApp = 1, MoMoWallet = 2) nên consumer cast trực tiếp được.
/// </summary>
public enum RidePaymentMethod : short
{
    MoMoApp = 1,
    MoMoWallet = 2
}
