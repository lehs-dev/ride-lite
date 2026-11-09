using Microsoft.Extensions.Options;
using TripService.Domain.Services;

namespace TripService.Infrastructure.Pricing;

public class TripPricingOptions
{
    public const string SectionName = "TripPricing";

    /// <summary>Giá mở cửa (VND).</summary>
    public decimal BaseFareVnd { get; set; } = 12_000m;

    /// <summary>Đơn giá mỗi km (VND).</summary>
    public decimal PerKmVnd { get; set; } = 13_500m;

    /// <summary>Làm tròn tới bội số này để tiền VND không lẻ.</summary>
    public int RoundToVnd { get; set; } = 500;
}

/// <summary>
/// Cước tính server-side bằng haversine — client không tự POST giá.
/// Không gọi MatchingService (giữ scope bài toán), chỉ cần đủ để luồng thanh toán chạy được.
/// </summary>
public sealed class HaversineFareCalculator : IFareCalculator
{
    private const double EarthRadiusKm = 6371.0;

    private readonly TripPricingOptions _options;

    public HaversineFareCalculator(IOptions<TripPricingOptions> options)
    {
        _options = options.Value;
    }

    public decimal Calculate(decimal pickupLatitude, decimal pickupLongitude,
        decimal destinationLatitude, decimal destinationLongitude)
    {
        var distanceKm = (decimal)HaversineKm(
            ToRadians(pickupLatitude), ToRadians(pickupLongitude),
            ToRadians(destinationLatitude), ToRadians(destinationLongitude));

        var raw = _options.BaseFareVnd + distanceKm * _options.PerKmVnd;
        return Round(raw, _options.RoundToVnd);
    }

    internal static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = lat2 - lat1;
        var dLon = lon2 - lon1;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(decimal degrees) => (double)degrees * Math.PI / 180.0;

    private static decimal Round(decimal amount, int step)
    {
        if (step <= 0)
            return decimal.Round(amount, 0);
        return decimal.Round(amount / step, 0, MidpointRounding.AwayFromZero) * step;
    }
}
