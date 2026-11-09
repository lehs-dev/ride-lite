using MassTransit;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Contracts.Events;
using TripService.Domain.Entities;
using TripService.Infrastructure.Persistence;

namespace TripService.Infrastructure.Messaging;

/// <summary>
/// Tiêu thụ PaymentFailed (M5-08): ghi dấu "cần xử lý" nhưng GIỮ trip ở PaymentPending —
/// không đánh dấu Paid, không hoàn tác việc đã chở khách. Event đến sau PaymentSettled
/// không được hạ cấp trip đã Paid.
///
/// Không thêm cột nào vào TripDB: TripDB chỉ có EnsureCreated (không migrations), nên mọi
/// cột mới sẽ vô hình trên DB dev đang chạy. Mã lỗi nằm ở Payment.FailureCode (PayDB) và
/// ở TripStatusHistory (bản append-only, đủ cho audit và cho read model sau này).
///
/// Event đến muộn/trạng thái lạ: Paid/Cancelled/Failed → ack + claim inbox; pre-PaymentPending
/// → ack im lặng không ghi history. Chỉ trip không tồn tại mới throw (vi phạm hợp đồng thật).
/// </summary>
public sealed class PaymentFailedConsumer : IConsumer<PaymentFailed>
{
    public const string InboxConsumerName = "trip-service.payment-failed";

    private readonly TripDbContext _db;
    private readonly ILogger<PaymentFailedConsumer> _logger;

    public PaymentFailedConsumer(TripDbContext db, ILogger<PaymentFailedConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentFailed> context)
    {
        var message = context.Message;
        var businessKey = $"{message.TripId}:{message.PaymentRequestKey}";

        if (await _db.ProcessedEvents.AnyAsync(
                e => e.ConsumerName == InboxConsumerName && e.BusinessKey == businessKey,
                context.CancellationToken))
        {
            _logger.LogInformation("Bỏ qua PaymentFailed duplicate cho TripId={TripId}", message.TripId);
            return;
        }

        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.Id == message.TripId, context.CancellationToken)
            // Trip chưa có: throw để redelivery rồi rơi vào _error, không ack mù.
            ?? throw new KeyNotFoundException($"Không tìm thấy trip {message.TripId} cho PaymentFailed.");

        switch (trip.Status)
        {
            case TripStatus.Paid:
            case TripStatus.Cancelled:
                // Đã trả tiền hoặc đã hủy: thất bại này không còn hiệu lực nghiệp vụ.
                _logger.LogInformation("Trip {TripId} đang ở {Status}, bỏ qua PaymentFailed.",
                    trip.Id, trip.Status);
                await ClaimInboxAsync(message, businessKey, context.CancellationToken);
                return;

            case TripStatus.PaymentPending:
                break;

            case TripStatus.Failed:
                // Trip đã bị đánh dấu Failed: ghi nhận thêm một lần failed nữa cũng không đổi gì.
                _logger.LogInformation("Trip {TripId} đã Failed, bỏ qua PaymentFailed ({ReasonCode}).",
                    trip.Id, message.ReasonCode);
                await ClaimInboxAsync(message, businessKey, context.CancellationToken);
                return;

            default:
                // Requested..DroppedOff: trip chưa mint nghĩa vụ thanh toán, nên "thất bại" này
                // đến từ một state cũ của PayDB (vd rider retry trong lúc trip vừa reset). Không
                // có nghiệp vụ nào để ghi và cũng không có gì sai hợp đồng — ack im lặng, đừng
                // nhét event vào _error chỉ vì race giữa hai service.
                _logger.LogWarning(
                    "Nhận PaymentFailed cho trip {TripId} ở trạng thái {Status} (chưa PaymentPending) — bỏ qua, không ghi history.",
                    trip.Id, trip.Status);
                return;
        }

        var attempts = message.AttemptCount > 0 ? $" sau {message.AttemptCount} lần thử" : string.Empty;
        var reason = message.RetryExhausted
            ? $"Thanh toán thất bại {message.ReasonCode}{attempts} (đã hết lượt retry)"
            : $"Thanh toán thất bại {message.ReasonCode}{attempts}";

        _db.TripStatusHistories.Add(new TripStatusHistory
        {
            TripId = trip.Id,
            FromStatus = trip.Status,
            ToStatus = trip.Status, // giữ nguyên PaymentPending — trip vẫn chờ thanh toán
            ActorType = "Consumer",
            Reason = reason,
            EventId = message.EventId
        });

        await ClaimInboxAsync(message, businessKey, context.CancellationToken);

        _logger.LogWarning("Trip {TripId} ghi nhận thanh toán thất bại ({Reason}), vẫn PaymentPending.",
            trip.Id, reason);
    }

    private async Task ClaimInboxAsync(PaymentFailed message, string businessKey, CancellationToken cancellationToken)
    {
        _db.ProcessedEvents.Add(new ProcessedEvent
        {
            ConsumerName = InboxConsumerName,
            BusinessKey = businessKey,
            MessageId = message.EventId
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateInboxRow(ex))
        {
            // Hai delivery song song của cùng một business key: bản kia đã ghi, bản này ack là đủ.
            _logger.LogInformation("Inbox đã có PaymentFailed {BusinessKey}, coi như đã xử lý.", businessKey);
        }
    }

    /// <summary>PK ProcessedEvents chặn trùng claim — provider-specific, chỉ nhận diện qua message.</summary>
    private static bool IsDuplicateInboxRow(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("Violation of primary key", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class PaymentFailedConsumerDefinition : ConsumerDefinition<PaymentFailedConsumer>
{
    public const string QueueName = "trip-service.payment-failed";

    public PaymentFailedConsumerDefinition()
    {
        EndpointName = QueueName;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PaymentFailedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry =>
        {
            retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
            // Trạng thái ngoài contract là lỗi vĩnh viễn — đừng quay vòng.
            retry.Ignore<InvalidOperationException>();
        });
    }
}
