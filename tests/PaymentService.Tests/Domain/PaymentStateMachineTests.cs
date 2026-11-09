using PaymentService.Domain;
using PaymentService.Domain.Entities;

namespace PaymentService.Tests.Domain;
public class PaymentStateMachineTests
{
    private static Payment NewPayment() => Payment.CreateFromRide(
        tripId: Guid.NewGuid(),
        paymentRequestKey: "trip-key",
        riderId: Guid.NewGuid(),
        driverId: null,
        amount: 95_000m,
        currency: "VND",
        method: PaymentMethod.MoMoApp,
        correlationId: null);

    [Fact]
    public void CreateFromRide_StartsInCreated()
    {
        var payment = NewPayment();

        Assert.Equal(PaymentStatus.Created, payment.Status);
        Assert.Equal(0, payment.AttemptCount);
    }

    [Fact]
    public void ChangeMethod_OnOpenPayment_UpdatesMethod()
    {
        var payment = NewPayment();

        payment.ChangeMethod(PaymentMethod.MoMoWallet);

        Assert.Equal(PaymentMethod.MoMoWallet, payment.Method);
    }

    [Fact]
    public void ChangeMethod_OnCharging_Conflict()
    {
        // Phiên gateway đang chạy với intent theo method cũ — đổi giữa chừng là 409.
        var payment = NewPayment();
        payment.MarkCharging();

        Assert.Throws<InvalidPaymentTransitionException>(() => payment.ChangeMethod(PaymentMethod.MoMoWallet));
    }

    [Fact]
    public void ChangeMethod_OnSettled_Conflict()
    {
        var payment = NewPayment();
        payment.MarkAuthorized();
        payment.MarkSettled(Guid.NewGuid());

        Assert.Throws<InvalidPaymentTransitionException>(() => payment.ChangeMethod(PaymentMethod.MoMoWallet));
    }

    [Fact]
    public void AuthorizeThenSettle_ReachesSettled()
    {
        var payment = NewPayment();
        var eventId = Guid.NewGuid();

        payment.MarkAuthorized();
        payment.MarkSettled(eventId);

        Assert.Equal(PaymentStatus.Settled, payment.Status);
        Assert.NotNull(payment.SettledAtUtc);
        Assert.True(payment.IsClosed);
    }

    [Fact]
    public void SettleTwiceSameEvent_IsIdempotent()
    {
        var payment = NewPayment();
        var eventId = Guid.NewGuid();
        payment.MarkAuthorized();
        payment.MarkSettled(eventId);
        var settledAt = payment.SettledAtUtc;

        payment.MarkSettled(eventId);

        Assert.Equal(PaymentStatus.Settled, payment.Status);
        Assert.Equal(settledAt, payment.SettledAtUtc);
    }

    [Fact]
    public void SettleWithDifferentEvent_Throws()
    {
        var payment = NewPayment();
        payment.MarkAuthorized();
        payment.MarkSettled(Guid.NewGuid());

        Assert.Throws<InvalidPaymentTransitionException>(() => payment.MarkSettled(Guid.NewGuid()));
    }

    [Fact]
    public void SettleFromCreated_Throws()
    {
        var payment = NewPayment();

        Assert.Throws<InvalidPaymentTransitionException>(() => payment.MarkSettled(Guid.NewGuid()));
    }

    [Fact]
    public void FailedIsTerminal_NoDowngradeOrResettle()
    {
        var payment = NewPayment();
        payment.MarkFailed("INSUFFICIENT_FUNDS", "không đủ số dư");

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Throws<InvalidPaymentTransitionException>(() => payment.MarkAuthorized());
        Assert.Throws<InvalidPaymentTransitionException>(
            () => payment.MarkPendingRetry("X", "y"));
    }

    [Fact]
    public void SettledCannotBeMarkedFailed_LateFailureMustNotDemote()
    {
        var payment = NewPayment();
        payment.MarkAuthorized();
        payment.MarkSettled(Guid.NewGuid());

        Assert.Throws<InvalidPaymentTransitionException>(() => payment.MarkFailed("LATE", "muộn"));
    }

    [Fact]
    public void PendingRetry_IncrementsAttemptAndStaysMutable()
    {
        var payment = NewPayment();

        payment.MarkPendingRetry("GATEWAY_UNAVAILABLE", "lỗi mạng");

        Assert.Equal(PaymentStatus.PendingRetry, payment.Status);
        Assert.Equal(1, payment.AttemptCount);
        payment.MarkAuthorized();
        Assert.Equal(PaymentStatus.Authorized, payment.Status);
    }

    [Fact]
    public void CreateFromRide_BlankKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => Payment.CreateFromRide(
            Guid.NewGuid(), "", Guid.NewGuid(), null, 10_000m, "VND", PaymentMethod.MoMoApp, null));
    }
}