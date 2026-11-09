namespace PaymentService.Domain;

/// <summary>Nghiệp vụ không cho phép chuyển payment sang trạng thái này.</summary>
public sealed class InvalidPaymentTransitionException : InvalidOperationException
{
    public InvalidPaymentTransitionException(string message) : base(message)
    {
    }
}
