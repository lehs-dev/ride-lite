namespace PaymentService.Domain.Entities;

/// <summary>
/// Phương thức thanh toán của một nghĩa vụ. Giá trị số là hợp đồng đã chốt: chúng được
/// lưu nguyên vẹn trong cột <c>Method</c> (smallint) của PayDB và serialize dạng số trong
/// DTO, nên chỉ được THÊM thành viên mới ở cuối — không đổi số, không xóa, không tái dùng.
/// Tập giá trị phải giữ khớp 1-1 với SwiftRide.Contracts.Events.RidePaymentMethod để consumer
/// cast trực tiếp; thành viên chỉ có ở đây (vd Cash) là kênh chỉ chọn qua REST, chưa phát hành
/// qua event.
/// </summary>
public enum PaymentMethod : short
{
    /// <summary>Mặc định lịch sử: ví MoMo mở từ app (deeplink).</summary>
    MoMoApp = 1,

    /// <summary>Trừ trực tiếp trong ví MoMo.</summary>
    MoMoWallet = 2,

    /// <summary>
    /// Thu tiền mặt lúc trả khách. Nghĩa vụ vẫn được ghi sổ ở PayDB để đối soát, nhưng
    /// backend KHÔNG gọi cổng điện tử (xem <see cref="PaymentPolicy.IsOfflineCollection"/>).
    /// Thêm cho P1-1: method phải đến từ event/REST thay vì hardcode MoMoApp.
    /// CHƯA tới được rider qua RideCompleted vì SwiftRide.Contracts.Events.RidePaymentMethod
    /// mới khai báo MoMoApp/MoMoWallet; đường vào hiện tại là PATCH /payments/{id}/method.
    /// </summary>
    Cash = 3
}
