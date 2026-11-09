using System.Text.Json;
using PaymentService.Domain;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Messaging;
using SwiftRide.Contracts.Events;

namespace PaymentService.Tests.Domain;

/// <summary>
/// P1-1 (method từ event được tôn trọng, không hardcode) và P1-6 (guard độ dài/dạng của
/// payload trước khi chạm DB). Test thuần domain + consumer static mapping — chạy trên mọi
/// host, không cần container hay EF provider.
/// </summary>
public class PaymentMethodAndValidationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static RideCompleted Event(RidePaymentMethod method) => new(
        EventId: Guid.NewGuid(),
        TripId: Guid.NewGuid(),
        RiderId: Guid.NewGuid(),
        DriverId: null,
        Amount: 95_000m,
        Currency: "VND",
        OccurredAtUtc: DateTime.UtcNow,
        CorrelationId: null,
        PaymentRequestKey: "trip:abc",
        PaymentMethod: method);

    [Theory]
    [InlineData(RidePaymentMethod.MoMoApp, PaymentMethod.MoMoApp)]
    [InlineData(RidePaymentMethod.MoMoWallet, PaymentMethod.MoMoWallet)]
    public void ContractValue_MapsOneToOneToDomainEnum(
        RidePaymentMethod contract, PaymentMethod expected) =>
        Assert.Equal(expected, RideCompletedConsumer.ResolveMethod(Event(contract)));

    [Fact]
    public void SchemaV1EventWithoutMethod_FallsBackToHistoricalDefault()
    {
        // Payload schema 1 thiếu field → deserialization trả default enum (MoMoApp),
        // tức đúng hành vi hardcode trước đây: không đổi kênh thu cho data đã lưu.
        var legacy = JsonSerializer.Deserialize<RideCompleted>(
            """
            {"eventId":"11111111-1111-1111-1111-111111111111",
             "tripId":"22222222-2222-2222-2222-222222222222",
             "riderId":"33333333-3333-3333-3333-333333333333",
             "driverId":null,"amount":95000,"currency":"VND",
             "occurredAtUtc":"2026-11-04T00:00:00Z","correlationId":null,
             "paymentRequestKey":"trip:x","schemaVersion":1}
            """, Web)!;

        Assert.Equal(PaymentMethod.MoMoApp, RideCompletedConsumer.ResolveMethod(legacy));
    }

    [Fact]
    public void UnknownContractMethod_IsRejectedNotSilentlyDowngraded()
    {
        // Trip thêm method mà PayDB chưa hỗ trợ → throw để message rơi _error,
        // không âm thầm charge theo kênh khác.
        Assert.Throws<InvalidOperationException>(() =>
            RideCompletedConsumer.ResolveMethod(Event((RidePaymentMethod)99)));
    }

    [Fact]
    public void Cash_IsSupportedAndClassifiedOffline()
    {
        Assert.True(PaymentPolicy.IsSupportedMethod(PaymentMethod.Cash));
        Assert.True(PaymentPolicy.IsOfflineCollection(PaymentMethod.Cash));
        Assert.False(PaymentPolicy.IsOfflineCollection(PaymentMethod.MoMoApp));
        Assert.False(PaymentPolicy.IsOfflineCollection(PaymentMethod.MoMoWallet));
    }

    [Fact]
    public void UndefinedEnumValue_IsNotSupported() =>
        Assert.False(PaymentPolicy.IsSupportedMethod((PaymentMethod)77));

    [Fact]
    public void CreateFromRide_RejectsUnsupportedMethod()
    {
        Assert.Throws<ArgumentException>(() => Payment.CreateFromRide(
            Guid.NewGuid(), "trip-key", Guid.NewGuid(), null,
            50_000m, "VND", (PaymentMethod)77, null));
    }

    [Fact]
    public void CreateFromRide_TrimsKeyAndNormalizesCurrency()
    {
        var payment = Payment.CreateFromRide(Guid.NewGuid(), " trip:key ", Guid.NewGuid(), null,
            50_000m, " vnd", PaymentMethod.MoMoWallet, null);

        Assert.Equal("trip:key", payment.PaymentRequestKey);
        Assert.Equal("VND", payment.Currency);
        Assert.Equal(PaymentMethod.MoMoWallet, payment.Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateFromRide_RejectsBlankKey(string key)
    {
        Assert.Throws<ArgumentException>(() => Payment.CreateFromRide(
            Guid.NewGuid(), key, Guid.NewGuid(), null,
            50_000m, "VND", PaymentMethod.MoMoApp, null));
    }

    [Fact]
    public void CreateFromRide_RejectsOverlongKey_BeforeDbCheckConstraint()
    {
        var key = new string('k', PaymentPolicy.MaxPaymentRequestKeyLength + 1);

        var ex = Assert.Throws<ArgumentException>(() => Payment.CreateFromRide(
            Guid.NewGuid(), key, Guid.NewGuid(), null, 50_000m, "VND", PaymentMethod.MoMoApp, null));

        Assert.Contains("PaymentRequestKey", ex.Message);
    }

    [Fact]
    public void CreateFromRide_MeasuresLengthOnRawKeyNotTrimmedValue()
    {
        // 2 khoảng trắng + đúng 128 ký tự thật = 130: phải bị chặn ở domain, vì chính chuỗi
        // 130 ký tự đó mới là thứ Postgres từ chối khi ghi varchar(128).
        var paddedKey = "  " + new string('k', PaymentPolicy.MaxPaymentRequestKeyLength);

        Assert.Throws<ArgumentException>(() => Payment.CreateFromRide(
            Guid.NewGuid(), paddedKey, Guid.NewGuid(), null, 50_000m, "VND",
            PaymentMethod.MoMoApp, null));
    }

    [Fact]
    public void CreateFromRide_RejectsWhitespaceOnlyCurrency()
    {
        Assert.Throws<ArgumentException>(() => Payment.CreateFromRide(
            Guid.NewGuid(), "trip-key", Guid.NewGuid(), null, 50_000m, "   ",
            PaymentMethod.MoMoApp, null));
    }

    [Fact]
    public void CreateFromRide_AcceptsKeyExactlyAtLimit()
    {
        var key = new string('k', PaymentPolicy.MaxPaymentRequestKeyLength);

        var payment = Payment.CreateFromRide(Guid.NewGuid(), key, Guid.NewGuid(), null,
            50_000m, "VND", PaymentMethod.Cash, null);

        Assert.Equal(key, payment.PaymentRequestKey);
    }

    [Theory]
    [InlineData("VN")]
    [InlineData("VNDX")]
    [InlineData("123")]
    [InlineData("V D")]
    public void CreateFromRide_RejectsMalformedCurrency(string currency)
    {
        Assert.Throws<ArgumentException>(() => Payment.CreateFromRide(
            Guid.NewGuid(), "trip-key", Guid.NewGuid(), null, 50_000m, currency,
            PaymentMethod.MoMoApp, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateFromRide_RejectsNonPositiveAmount(decimal amount)
    {
        Assert.Throws<ArgumentException>(() => Payment.CreateFromRide(
            Guid.NewGuid(), "trip-key", Guid.NewGuid(), null, amount, "VND",
            PaymentMethod.MoMoApp, null));
    }

    [Fact]
    public void ChangeMethod_RejectsUnsupportedValue_AndKeepsPrevious()
    {
        var payment = Payment.CreateFromRide(Guid.NewGuid(), "trip-key", Guid.NewGuid(), null,
            50_000m, "VND", PaymentMethod.MoMoApp, null);

        Assert.Throws<ArgumentException>(() => payment.ChangeMethod((PaymentMethod)77));
        Assert.Equal(PaymentMethod.MoMoApp, payment.Method);

        payment.ChangeMethod(PaymentMethod.Cash);
        Assert.Equal(PaymentMethod.Cash, payment.Method);
    }

    [Fact]
    public void MarkCashCollected_OnlyAppliesToCashObligation()
    {
        var cash = Payment.CreateFromRide(Guid.NewGuid(), "trip-key", Guid.NewGuid(), null,
            50_000m, "VND", PaymentMethod.Cash, null);
        cash.MarkCashCollected();

        Assert.Equal(PaymentStatus.Authorized, cash.Status);

        var electronic = Payment.CreateFromRide(Guid.NewGuid(), "trip-key2", Guid.NewGuid(), null,
            50_000m, "VND", PaymentMethod.MoMoApp, null);
        Assert.Throws<InvalidPaymentTransitionException>(() => electronic.MarkCashCollected());
    }

    [Fact]
    public void MarkCashCollected_RespectsClosedState()
    {
        var cash = Payment.CreateFromRide(Guid.NewGuid(), "trip-key", Guid.NewGuid(), null,
            50_000m, "VND", PaymentMethod.Cash, null);
        cash.MarkFailed("USER_CANCELLED", "hủy");

        Assert.Throws<InvalidPaymentTransitionException>(() => cash.MarkCashCollected());
    }
}
