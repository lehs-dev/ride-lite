using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Application.Abstractions;
using PaymentService.Infrastructure.Payment;

namespace PaymentService.Tests.Domain;

public class MoMoSandboxGatewayTests
{
    private static MoMoSandboxGateway BuildGateway(decimal maxAmount = 5_000_000m) => new(
        Options.Create(new MoMoSandboxOptions { MaxAmountVnd = maxAmount, SimulatedLatencyMs = 0 }),
        NullLogger<MoMoSandboxGateway>.Instance);

    private static PaymentIntent Intent(decimal amount, string currency = "VND") => new(
        Guid.NewGuid(), Guid.NewGuid(), "trip-key", amount, currency, PaymentMethodKind.MoMoApp);

    [Fact]
    public async Task Charge_NormalAmount_SucceedsWithChecksum()
    {
        var gateway = BuildGateway();

        var result = await gateway.ChargeAsync(Intent(95_000m));

        Assert.True(result.Success);
        Assert.Equal(MoMoSandboxGateway.CodeSuccess, result.Code);
        Assert.StartsWith("SR", result.OrderRef);
        Assert.NotNull(result.Checksum);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public async Task Charge_NonPositiveAmount_InvalidAmountNotRetryable(decimal amount)
    {
        var gateway = BuildGateway();

        var result = await gateway.ChargeAsync(Intent(amount));

        Assert.False(result.Success);
        Assert.Equal(MoMoSandboxGateway.CodeInvalidAmount, result.Code);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task Charge_WrongCurrency_InvalidAmount()
    {
        var gateway = BuildGateway();

        var result = await gateway.ChargeAsync(Intent(50_000m, "USD"));

        Assert.False(result.Success);
        Assert.Equal(MoMoSandboxGateway.CodeInvalidAmount, result.Code);
    }

    [Fact]
    public async Task Charge_AboveLimit_AmountLimitExceeded()
    {
        var gateway = BuildGateway(maxAmount: 100_000m);

        var result = await gateway.ChargeAsync(Intent(150_000m));

        Assert.False(result.Success);
        Assert.Equal(MoMoSandboxGateway.CodeAmountLimit, result.Code);
    }

    [Theory]
    [InlineData(113_003)]
    [InlineData(213_013)]
    public async Task Charge_InsufficientFundsTrigger_FailsNonRetryable(decimal amount)
    {
        var gateway = BuildGateway();

        var result = await gateway.ChargeAsync(Intent(amount));

        Assert.False(result.Success);
        Assert.Equal(MoMoSandboxGateway.CodeInsufficientFunds, result.Code);
        Assert.False(result.Retryable);
    }

    [Theory]
    [InlineData(137_007)]
    [InlineData(237_027)]
    public async Task Charge_UnavailableTrigger_ThrowsHttpRequest(decimal amount)
    {
        var gateway = BuildGateway();

        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.ChargeAsync(Intent(amount)));
    }
}