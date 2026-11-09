using PaymentService.Domain;
using PaymentService.Domain.Entities;

namespace PaymentService.Tests.Consumers;

/// <summary>Nghiệp vụ consumer chạy mà không cần broker/DB — container chỉ dành cho integration test.</summary>
public class RideCompletedInboxPolicyTests
{
    [Theory]
    [InlineData(PaymentStatus.Settled)]
    [InlineData(PaymentStatus.Failed)]
    public void ClosedPayment_IsNotReopenedByRedelivery(PaymentStatus status)
    {
        var payment = CloseTo(status);

        Assert.True(payment.IsClosed);
        Assert.ThrowsAny<Exception>(() => payment.MarkAuthorized());
    }

    [Fact]
    public void OpenPayment_CanStillBeAdvanced()
    {
        var payment = Payment.CreateFromRide(Guid.NewGuid(), "trip-key", Guid.NewGuid(), null,
            95_000m, "VND", PaymentMethod.MoMoApp, null);

        Assert.False(payment.IsClosed);
        payment.MarkAuthorized();
        Assert.Equal(PaymentStatus.Authorized, payment.Status);
    }

    [Theory]
    [InlineData(PaymentStatus.Settled)]
    [InlineData(PaymentStatus.Failed)]
    public void FinalOutcome_IsSafeToClaimInbox(PaymentStatus status)
    {
        // P0-1: chỉ những trạng thái này được phép lại inbox row. Claim ở Created/Charging
        // chính là lỗi khiến redelivery bị ACK mù.
        var payment = CloseTo(status);

        Assert.True(payment.IsRideOutcomeFinal);
    }

    [Fact]
    public void Charging_IsNotFinalSoRedeliveryStillEvaluates()
    {
        var payment = Payment.CreateFromRide(Guid.NewGuid(), "trip-key", Guid.NewGuid(), null,
            95_000m, "VND", PaymentMethod.MoMoApp, null);
        payment.MarkCharging();

        Assert.False(payment.IsClosed);
        Assert.False(payment.IsRideOutcomeFinal);

        // Phiên thứ hai trên cùng nghĩa vụ phải bị từ ngay ở domain, không cần đụng DB.
        Assert.Throws<InvalidPaymentTransitionException>(() => payment.MarkCharging());
    }

    [Fact]
    public void ExpiredChargingLease_FallsBackToPendingRetryForReclaim()
    {
        var payment = Payment.CreateFromRide(Guid.NewGuid(), "trip-key", Guid.NewGuid(), null,
            95_000m, "VND", PaymentMethod.MoMoApp, null);
        payment.MarkCharging();

        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        payment.UpdatedAtUtc = now - PaymentPolicy.ChargingLease - TimeSpan.FromMinutes(1);

        Assert.True(payment.IsChargingLeaseExpired(now));

        payment.MarkChargingLeaseExpired();

        Assert.Equal(PaymentStatus.PendingRetry, payment.Status);
        Assert.Equal(PaymentPolicy.ChargingLeaseExpiredCode, payment.FailureCode);
        Assert.Equal(1, payment.AttemptCount);
    }

    [Fact]
    public void ManualRetry_ReopensOnlyFailedPayments()
    {
        var failed = Payment.CreateFromRide(Guid.NewGuid(), "trip-key", Guid.NewGuid(), null,
            95_000m, "VND", PaymentMethod.MoMoApp, null);
        failed.MarkFailed("INSUFFICIENT_FUNDS", "sandbox");
        failed.PrepareForManualRetry();

        Assert.Equal(PaymentStatus.Created, failed.Status);
        Assert.Equal(0, failed.AttemptCount);
        Assert.False(failed.IsRideOutcomeFinal);

        var settled = CloseTo(PaymentStatus.Settled);
        Assert.Throws<InvalidPaymentTransitionException>(() => settled.PrepareForManualRetry());
    }

    private static Payment CloseTo(PaymentStatus status)
    {
        var payment = Payment.CreateFromRide(Guid.NewGuid(), "trip-key", Guid.NewGuid(), null,
            95_000m, "VND", PaymentMethod.MoMoApp, null);

        switch (status)
        {
            case PaymentStatus.Settled:
                payment.MarkAuthorized();
                payment.MarkSettled(Guid.NewGuid());
                break;
            case PaymentStatus.Failed:
                payment.MarkFailed("INSUFFICIENT_FUNDS", "sandbox");
                break;
        }

        return payment;
    }
}