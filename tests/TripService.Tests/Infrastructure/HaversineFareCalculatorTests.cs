using TripService.Domain.Entities;
using TripService.Infrastructure.Pricing;
using Microsoft.Extensions.Options;

namespace TripService.Tests.Infrastructure;

public class HaversineFareCalculatorTests
{
    private static HaversineFareCalculator Build(decimal perKm = 13_500m, decimal @base = 12_000m) =>
        new(Options.Create(new TripPricingOptions
        {
            BaseFareVnd = @base,
            PerKmVnd = perKm,
            RoundToVnd = 500
        }));

    [Fact]
    public void SamePoint_ReturnsBaseFare()
    {
        var calculator = Build();

        var fare = calculator.Calculate(10.7758m, 106.7019m, 10.7758m, 106.7019m);

        Assert.Equal(12_000m, fare);
    }

    [Fact]
    public void OneKilometer_AddsPerKmAndRoundsToStep()
    {
        var calculator = Build(perKm: 10_000m, @base: 0m);

        // ~0.008983 độ vĩ tuyến ≈ 1 km
        var fare = calculator.Calculate(10.0m, 106.0m, 10.008983m, 106.0m);

        Assert.Equal(10_000m, fare);
    }

    [Fact]
    public void Fare_RoundedToFiveHundred()
    {
        var calculator = Build();

        var fare = calculator.Calculate(10.7758m, 106.7019m, 10.8234m, 106.6600m);

        Assert.Equal(0, fare % 500);
        Assert.True(fare > 12_000m);
    }
}
