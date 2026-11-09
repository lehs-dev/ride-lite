using MassTransit;
using PaymentService.Domain;

namespace PaymentService.Infrastructure.Messaging;

/// <summary>
/// Queue name cố định để đọc được trong RabbitMQ management UI. Retry in-memory ngắn;
/// lỗi nghiệp vụ vĩnh viễn (mâu thuẫn số tiền, payment đã chốt) rơi thẳng vào _error.
/// Độ bền thật đến từ outbox/inbox tables, không phải in-memory outbox.
/// </summary>
public sealed class RideCompletedConsumerDefinition : ConsumerDefinition<RideCompletedConsumer>
{
    public const string QueueName = "payment-service.ride-completed";

    public RideCompletedConsumerDefinition()
    {
        EndpointName = QueueName;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<RideCompletedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry =>
        {
            retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
            retry.Ignore<InvalidPaymentTransitionException>();
            retry.Ignore<InvalidOperationException>();
        });
    }
}
