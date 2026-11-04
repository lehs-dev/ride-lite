using System.ComponentModel.DataAnnotations;
namespace TripService.Application.DTOs;

public class CreateTripRequest
{
    [Required(ErrorMessage = "Bạn cần cung cấp vĩ độ điểm đón.")]
    [Range(-90d, 90d, ErrorMessage = "Vĩ độ điểm đón phải từ -90 đến 90.")]
    public double? FromLat { get; set; }

    [Required(ErrorMessage = "Bạn cần cung cấp kinh độ điểm đón.")]
    [Range(-180d, 180d, ErrorMessage = "Kinh độ điểm đón phải từ -180 đến 180.")]
    public double? FromLon { get; set; }

    [Required(ErrorMessage = "Bạn cần cung cấp vĩ độ điểm đến.")]
    [Range(-90d, 90d, ErrorMessage = "Vĩ độ điểm đến phải từ -90 đến 90.")]
    public double? ToLat { get; set; }

    [Required(ErrorMessage = "Bạn cần cung cấp kinh độ điểm đến.")]
    [Range(-180d, 180d, ErrorMessage = "Kinh độ điểm đến phải từ -180 đến 180.")]
    public double? ToLon { get; set; }

    [Required(ErrorMessage = "Bạn cần nhập địa chỉ điểm đón.")]
    [StringLength(255, ErrorMessage = "Địa chỉ điểm đón tối đa 255 ký tự.")]
    public string FromAddress { get; set; } = string.Empty;

    [Required(ErrorMessage = "Bạn cần nhập địa chỉ điểm đến.")]
    [StringLength(255, ErrorMessage = "Địa chỉ điểm đến tối đa 255 ký tự.")]
    public string ToAddress { get; set; } = string.Empty;


    [Required(ErrorMessage = "Bạn cần chọn phương thức thanh toán.")]
    [StringLength(255, ErrorMessage = "Phương thức thanh toán tối đa 255 ký tự.")]
    public string PaymentMethod { get; set; } = string.Empty;
}