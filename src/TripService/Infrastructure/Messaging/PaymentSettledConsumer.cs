using MassTransit;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Contracts.Events;
using TripService.Domain;
using TripService.Domain.Entities;
using TripService.Infrastructure.Persistence;

namespace TripService.Infrastructure.Messaging;

/// <summary>
/// Tiêu thụ PaymentSettled: dedup theo (TripId, PaymentRequestKey, PaymentId) — một trip có
/// thể được settle nhiều lần nếu rider retry với payment mới. Chỉ chuyển PaymentPending → Paid;
/// event đến muộn cho trip Cancelled/Failed/pre-dropoff là no-op im lặng chứ không rơi _error.
/// History + inbox row commit cùng một transaction (M5-06/M5-07). Không có API public nào cho bước này.
/// </summary>
public sealed class PaymentSettledConsumer : IConsumer<PaymentSettled>
{
    public const string InboxConsumerName = "trip-service.payment-settled";

    private readonly TripDbContext _db;
    private readonly ILogger<PaymentSettledConsumer> _logger;

    public PaymentSettledConsumer(TripDbContext db, ILogger<PaymentSettledConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentSettled> context)
    {
        var message = context.Message;
        // Dedup theo (trip, payment request key, payment): PaymentService có thể settle lại
        // cùng một trip với payment khác sau khi rider retry — mỗi lần settle là một nghiệp vụ
        // riêng, redelivery của chính nó mới là bản sao cần chặn.
        var inboxKey = $"{message.TripId}:{message.PaymentRequestKey}:{message.PaymentId}";

        if (await _db.ProcessedEvents.AnyAsync(
                e => e.ConsumerName == InboxConsumerName && e.BusinessKey == inboxKey,
                context.CancellationToken))
        {
            _logger.LogInformation("Bỏ qua PaymentSettled duplicate cho TripId={TripId}", message.TripId);
            return;
        }

        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.Id == message.TripId, context.CancellationToken)
            // Trip chưa tồn tại: throw để retry rồi rơi vào _error, không ack mù quáng.
            ?? throw new KeyNotFoundException($"Không tìm thấy trip {message.TripId} cho PaymentSettled.");

        if (trip.EstimatedPrice != message.Amount ||
            !string.Equals(trip.Currency, message.Currency, StringComparison.OrdinalIgnoreCase))
        {
            // Trip chưa chốt PaymentPending mà đã nhận settled với số tiền khác: reader có thể
            // thấy trip pre-dropoff trong lúc writer commit — so giá trị nháp rồi bỏ qua,
            // đừng đưa event hợp lệ vào _error.
            if (trip.Status is TripStatus.Requested or TripStatus.DriverAccepted or TripStatus.EnRoute
                    or TripStatus.PickedUp or TripStatus.DroppedOff)
            {
                _logger.LogWarning(
                    "Bỏ qua PaymentSettled cho trip {TripId} ở {Status}: trip đang {Amount} {Currency}, event báo {EventAmount} {EventCurrency}.",
                    trip.Id, trip.Status, trip.EstimatedPrice, trip.Currency, message.Amount, message.Currency);
                return;
            }

            throw new InvalidOperationException(
                $"PaymentSettled mâu thuẫn: trip {trip.Id} chốt {trip.EstimatedPrice} {trip.Currency}, event báo {message.Amount} {message.Currency}.");
        }

        // Trip đã settle trước đó (bản redelivery, hoặc payment khác settle sau khi rider retry):
        // không còn nghiệp vụ nào phải làm. Claim inbox để redelivery dừng hẳn — trip Paid là
        // trạng thái cuối, không bao giờ hạ cấp.
        if (trip.Status == TripStatus.Paid)
        {
            _logger.LogInformation("Trip {TripId} đã Paid, bỏ qua PaymentSettled (payment {PaymentId}).",
                trip.Id, message.PaymentId);

            await CommitWithInboxAsync(message, inboxKey, context.CancellationToken);
            return;
        }

        var from = trip.Status;
        var changed = trip.TryMarkPaid(
            message.PaymentId, message.PaymentRequestKey, message.TransactionId?.ToString(),
            toleratePreDropoff: true);

        // Event đến muộn cho trip Cancelled/Failed/pre-dropoff: không có nghiệp vụ nào xảy ra,
        // nhưng cũng không có gì sai — ack im lặng (không claim inbox vì việc chưa từng làm)
        // thay vì đưa event hợp lệ vào _error.
        if (!changed)
        {
            _logger.LogInformation(
                "PaymentSettled cho trip {TripId} bị bỏ qua (trip ở {Status}).", trip.Id, from);
            return;
        }

        _db.TripStatusHistories.Add(new TripStatusHistory
        {
            TripId = trip.Id,
            FromStatus = from,
            ToStatus = TripStatus.Paid,
            ActorType = "Consumer",
            Reason = "PaymentService xác nhận đã thu tiền",
            EventId = message.EventId
        });

        await CommitWithInboxAsync(message, inboxKey, context.CancellationToken);

        _logger.LogInformation("Trip {TripId} chuyển sang Paid (payment {PaymentId}).", trip.Id, message.PaymentId);
    }

    /// <summary>
    /// Claim inbox và ghi history trong CÙNG một SaveChanges: PK (ConsumerName, BusinessKey)
    /// chặn hai delivery song tạo trùng history, và redelivery sau đó đi qua dedup đầu Consume.
    /// </summary>
    private async Task CommitWithInboxAsync(PaymentSettled message, string inboxKey, CancellationToken cancellationToken)
    {
        _db.ProcessedEvents.Add(new ProcessedEvent
        {
            ConsumerName = InboxConsumerName,
            BusinessKey = inboxKey,
            MessageId = message.EventId
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateInboxRow(ex))
        {
            // Hai delivery song song của cùng một message: bản thắng đã ghi nghiệp vụ, bản này
            // chỉ việc ack — không phải lỗi, đừng đưa vào _error.
            _logger.LogInformation("Inbox đã có PaymentSettled {InboxKey}, coi như đã xử lý.", inboxKey);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Đã có request khác ghi trip; redelivery sẽ đi qua dedup nên không tạo nghiệp vụ lặp.
            throw new InvalidTripTransitionException("Xung đột cập nhật trip, sẽ thử lại.");
        }
    }

    /// <summary>PK ProcessedEvents chặn trùng claim — provider-specific, chỉ nhận diện qua message.</summary>
    private static bool IsDuplicateInboxRow(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("Violation of primary key", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class PaymentSettledConsumerDefinition : ConsumerDefinition<PaymentSettledConsumer>
{
    public const string QueueName = "trip-service.payment-settled";

    public PaymentSettledConsumerDefinition()
    {
        EndpointName = QueueName;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PaymentSettledConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry =>
        {
            retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
            // Dữ liệu mâu thuẫn là lỗi vĩnh viễn — đừng quay vòng.
            retry.Ignore<InvalidOperationException>();
        });
    }
}
