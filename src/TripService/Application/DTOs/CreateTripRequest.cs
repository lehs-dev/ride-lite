namespace TripService.Application.DTOs;

public class CreateTripRequest
{
    public double FromLat { get; set; }
    public double FromLon { get; set; }
    public string FromAddress { get; set; } = string.Empty;

    public double ToLat { get; set; }
    public double ToLon { get; set; }
    public string ToAddress { get; set; } = string.Empty;

    public string PaymentMethod { get; set; } = string.Empty;
}