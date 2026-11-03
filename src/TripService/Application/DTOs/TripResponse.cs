namespace TripService.Application.DTOs;

public class TripResponse
{
    public Guid Id { get; set; }
    public Guid RiderId { get; set; }
    public Guid? DriverId { get; set; }

    public string Status { get; set; } = string.Empty;

    public double FromLat { get; set; }
    public double FromLon { get; set; }
    public string FromAddress { get; set; } = string.Empty;

    public double ToLat { get; set; }
    public double ToLon { get; set; }
    public string ToAddress { get; set; } = string.Empty;

    public double? Distance { get; set; }
    public double? EstimatedTime { get; set; }
    public decimal? Price { get; set; }

    public string PaymentMethod { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? PickUpTimeUtc { get; set; }
    public DateTime? DropOffTimeUtc { get; set; }
}