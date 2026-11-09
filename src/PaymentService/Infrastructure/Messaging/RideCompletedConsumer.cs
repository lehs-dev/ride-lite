using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentService.Domain;
using PaymentService.Infrastructure.Persistence;
using PaymentService.Infrastructure.Services;
using SwiftRide.Contracts.Events;

namespace PaymentService.Infrastructure.Messaging;

/// <summary>
/// Tiêu thụ RideCompleted: validate payload, đảm bảo nghĩa vụ tồn tại, rồi giao toàn bộ
/// việc charge cho PaymentProcessor — đường duy nhất được gọi gateway.
///
/// Inbox chỉ còn là fast-path + audit: nó CHỈ được ghi khi nghĩa vụ đã đạt kết quả chốt,
/// nên một bản tin redelivery sau crash (payment còn Created/PendingRetry/Charging) không
/// bao giờ bị ACK mù — đó chính là lỗi P0-1 mà phiên bản cũ mắc phải.
/// </summary>
public sealed class RideCompletedConsumer : IConsumer<RideCompleted>
{
    public const string InboxConsumerName = PaymentProcessor.InboxConsumer;

    private readonly PaymentDbContext _db;
    private readonly PaymentProcessor _processor;
    private readonly ILogger<RideCompletedConsumer> _logger;

    public RideCompletedConsumer(
        PaymentDbContext db,
        PaymentProcessor processor,
        ILogger<RideCompletedConsumer> logger)
    {
        _db = db;
        _processor = processor;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<RideCompleted> context)
    {
        var message = context.Message;
        var businessKey = $"{message.TripId}:{message.PaymentRequestKey}";

        // Key phải đúng quy ước mà TripService sẽ kiểm tra khi nhận PaymentSettled; nếu không,
        // payment settle xong nhưng trip vẫn từ chối key và kẹt PaymentPending.
        var expectedKey = EventContracts.BuildPaymentRequestKey(message.TripId);
        if (!string.Equals(message.PaymentRequestKey, expectedKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"RideCompleted mang PaymentRequestKey '{message.PaymentRequestKey}' không khớp quy ước " +
                $"'{expectedKey}' cho TripId={message.TripId}.");
        }

        if (await IsInboxClaimedAsync(businessKey, context.CancellationToken))
        {
            _logger.LogInformation("Bỏ qua RideCompleted duplicate cho TripId={TripId} (đã có kết quả chốt).",
                message.TripId);
            return;
        }

        var request = new ChargeRequest(
            message.TripId, message.PaymentRequestKey, message.RiderId, message.DriverId,
            message.Amount, message.Currency, ResolveMethod(message),
            ResolveCorrelation(context));

        // ResolveObligationAsync giữ method đã chốt trên nghĩa vụ (rider chọn qua REST trước
        // dropoff); event chỉ mang method mặc định khi tạo mới.
        var payment = await _processor.ResolveObligationAsync(request, context.CancellationToken);

        // EventId của RideCompleted làm MessageId cho PaymentSettled: hai bản tin trùng payload
        // phát ra event giống nhau, và unique index trên MessageId chặn phát lặp.
        await _processor.ChargeAsync(payment, message.EventId, businessKey, context.CancellationToken);
    }

    /// <summary>
    /// P1-1: phương thức thanh toán đến từ payload thay vì hardcode MoMoApp.
    ///
    /// Tương thích ngược: field là enum có giá trị mặc định MoMoApp, nên payload schema 1
    /// (thiếu field) deserialize về đúng MoMoApp — hành vi như bản hardcode. Numeric value của
    /// RidePaymentMethod khớp 1-1 với PaymentMethod phía PayDB nên cast trực tiếp; nhưng enum
    /// ngoài tập hỗ trợ (vd Trip thêm method mà PaymentService chưa biết) phải bị từ chối bằng
    /// InvalidOperationException → message rơi _error, không âm thầm charge nhầm kênh.
    /// </summary>
    public static Domain.Entities.PaymentMethod ResolveMethod(RideCompleted message)
    {
        if (!Enum.IsDefined(message.PaymentMethod))
            throw new InvalidOperationException(
                $"RideCompleted mang PaymentMethod không xác định ({(short)message.PaymentMethod}) " +
                $"cho TripId={message.TripId} — từ chối để tránh thu tiền sai kênh.");

        var method = (Domain.Entities.PaymentMethod)(short)message.PaymentMethod;
        if (!PaymentPolicy.IsSupportedMethod(method))
            throw new InvalidOperationException(
                $"RideCompleted mang PaymentMethod {message.PaymentMethod} chưa được PayDB hỗ trợ " +
                $"cho TripId={message.TripId}.");

        return method;
    }

    private Task<bool> IsInboxClaimedAsync(string businessKey, CancellationToken cancellationToken) =>
        _db.ProcessedEvents.AnyAsync(
            e => e.ConsumerName == InboxConsumerName && e.BusinessKey == businessKey, cancellationToken);

    /// <summary>Header CorrelationId của MassTransit thắng; fallback về field trong payload.</summary>
    private static string? ResolveCorrelation(ConsumeContext<RideCompleted> context)
    {
        if (context.CorrelationId is { } header)
            return header.ToString("N");

        return context.Message.CorrelationId?.ToString("N");
    }
}
