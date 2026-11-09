using Microsoft.EntityFrameworkCore;
using PaymentEntity = PaymentService.Domain.Entities.Payment;
using PaymentService.Application.Abstractions;
using PaymentService.Domain;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Payment;
using PaymentService.Infrastructure.Persistence;
using SwiftRide.Contracts.Events;

namespace PaymentService.Infrastructure.Services;

/// <summary>Kết quả một lượt xử lý charge — quyết định consumer/worker có còn việc phải làm không.</summary>
public enum ChargeOutcome
{
    Settled,
    Failed,

    /// <summary>Gateway lỗi tạm thời: payment ở PendingRetry, worker sẽ tiếp tục.</summary>
    RetryScheduled,

    /// <summary>
    /// Thu tiền mặt đã ghi sổ thành công — không có gateway call nào được thực hiện.
    /// Phân biệt với <see cref="Settled"/> để caller/audit biết kênh thu là offline.
    /// </summary>
    CollectedOffline,

    /// <summary>Một phiên khác đang charge (mất claim) — không được gọi gateway lần hai.</summary>
    InFlightElsewhere,

    /// <summary>Nghĩa vụ đã có kết quả chốt từ trước — không làm gì.</summary>
    AlreadyFinal
}

/// <summary>Dữ liệu nghĩa vụ thanh toán mà TripService chốt (fare do server tính, client không gửi).</summary>
public sealed record ChargeRequest(
    Guid TripId,
    string PaymentRequestKey,
    Guid RiderId,
    Guid? DriverId,
    decimal Amount,
    string Currency,
    PaymentMethod Method,
    string? CorrelationId);

/// <summary>
/// Lõi charge dùng chung cho CẢ BA đường vào (consumer RideCompleted, REST, background worker).
/// Trước đây mỗi đường có một bản sao logic với quy tắc inbox/retry riêng, và đó chính là nguồn
/// của lỗi thu tiền lặp + payment kẹt. Bất kỳ thay đổi nào về "khi nào được gọi gateway" phải
/// sửa ở đúng một chỗ này.
///
/// Bất biến:
/// 1. Inbox CHỈ được claim khi nghĩa vụ đạt kết quả chốt (Settled/Failed/đã chốt từ trước).
///    Kết quả trung gian (PendingRetry/Charging) không claim, nên redelivery tiếp tục được việc
///    thay vì ACK mù một payment đang kẹt.
/// 2. Trước khi gọi gateway phải claim bằng một UPDATE đổi Status thật; concurrency token trên
///    Status đảm bảo đúng một bên thắng. Bên thua trả InFlightElsewhere và không đụng gateway.
/// </summary>
public sealed class PaymentProcessor
{
    /// <summary>Tên inbox dùng cho nghĩa vụ RideCompleted — consumer đọc lại để fast-path.</summary>
    public const string InboxConsumer = "payment-service.ride-completed";

    private readonly PaymentDbContext _db;
    private readonly IPaymentGateway _gateway;
    private readonly IPaymentEventPublisher _publisher;
    private readonly ILogger<PaymentProcessor> _logger;

    public PaymentProcessor(
        PaymentDbContext db,
        IPaymentGateway gateway,
        IPaymentEventPublisher publisher,
        ILogger<PaymentProcessor> logger)
    {
        _db = db;
        _gateway = gateway;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>
    /// Tìm hoặc tạo nghĩa vụ theo (TripId, PaymentRequestKey). Payload mâu thuẫn số tiền/tiền tệ
    /// với nghĩa vụ đã chốt → InvalidOperationException để message rơi vào _error, không âm thầm sửa giá.
    /// </summary>
    public async Task<PaymentEntity> ResolveObligationAsync(
        ChargeRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await FindObligationAsync(request.TripId, request.PaymentRequestKey, cancellationToken);
        if (existing is not null)
            return VerifyOrThrow(existing, request);

        // Validate TRƯỚC khi chạm DB: payload sai (amount <= 0, key quá 128, currency không phải
        // ISO-4217, method ngoài enum) phải throw ArgumentException từ domain, không phải
        // DbUpdateException mù do CHECK/varchar violation. CreateFromRide là pure factory nên
        // gọi hai lần không có tác dụng phụ; object của lần pre-check chưa từng được Add.
        _ = PaymentEntity.CreateFromRide(
            request.TripId, request.PaymentRequestKey, request.RiderId, request.DriverId,
            request.Amount, request.Currency, request.Method, request.CorrelationId);

        var created = PaymentEntity.CreateFromRide(
            request.TripId, request.PaymentRequestKey, request.RiderId, request.DriverId,
            request.Amount, request.Currency, request.Method, request.CorrelationId);

        _db.Payments.Add(created);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return created;
        }
        catch (DbUpdateException)
        {
            // Đối thủ tạo cùng lúc (unique index TripId/PaymentRequestKey chặn) -> dùng row của họ.
            _db.ChangeTracker.Clear();
            var winner = await FindObligationAsync(request.TripId, request.PaymentRequestKey, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Mâu thuẫn tạo nghĩa vụ {request.PaymentRequestKey} nhưng không đọc lại được row.");
            return VerifyOrThrow(winner, request);
        }
    }

    public Task<PaymentEntity?> FindObligationAsync(
        Guid tripId,
        string paymentRequestKey,
        CancellationToken cancellationToken) =>
        _db.Payments.SingleOrDefaultAsync(
            p => p.TripId == tripId || p.PaymentRequestKey == paymentRequestKey, cancellationToken);

    /// <summary>
    /// Claim + gọi gateway + chốt kết quả. resultEventId là MessageId của event phát ra;
    /// inboxBusinessKey (nếu có) chỉ được ghi khi kết quả là chốt.
    /// </summary>
    public async Task<ChargeOutcome> ChargeAsync(
        PaymentEntity payment,
        Guid resultEventId,
        string? inboxBusinessKey,
        CancellationToken cancellationToken)
    {
        if (payment.IsRideOutcomeFinal)
        {
            await ClaimInboxAsync(inboxBusinessKey, resultEventId, cancellationToken);
            _logger.LogInformation("Payment {PaymentId} đã chốt ({Status}), không thu tiền lặp.",
                payment.Id, payment.Status);
            return ChargeOutcome.AlreadyFinal;
        }

        // P1-1: Cash là kênh offline — không có gateway call, nên cũng không có lease Charging.
        // Chốt trong cùng transaction với PaymentSettled + inbox claim, giống hệt đường điện tử.
        if (PaymentPolicy.IsOfflineCollection(payment.Method))
        {
            await SettleOfflineAsync(payment, resultEventId, inboxBusinessKey, cancellationToken);
            return ChargeOutcome.CollectedOffline;
        }

        if (!await TryClaimAsync(payment, cancellationToken))
            return ChargeOutcome.InFlightElsewhere;

        // Hết hạn mức retry mà worker vẫn thấy pending: chốt Failed + phát PaymentFailed.
        // Đặt ở đây (thay vì mỗi caller tự xử lý) để mọi đường vào đều có cùng hành vi.
        if (payment.AttemptCount >= PaymentPolicy.MaxGatewayAttempts)
        {
            await FailAsync(
                payment,
                payment.FailureCode ?? MoMoSandboxGateway.CodeGatewayUnavailable,
                "Hết lượt retry sandbox mà không có kết quả thành công.",
                retryExhausted: true,
                inboxBusinessKey,
                cancellationToken);
            return ChargeOutcome.Failed;
        }

        GatewayChargeResult result;
        try
        {
            result = await _gateway.ChargeAsync(BuildIntent(payment), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Trần retry do worker quyết định: ở đây chỉ đánh dấu PendingRetry + đếm attempt.
            // KHÔNG claim inbox — redelivery còn phải được xử lý tiếp.
            payment.MarkPendingRetry(MoMoSandboxGateway.CodeGatewayUnavailable, ex.Message);
            await SaveOrLoseAsync(cancellationToken);
            _logger.LogWarning(ex, "Gateway lỗi tạm thời cho TripId={TripId} (attempt {Attempt})",
                payment.TripId, payment.AttemptCount);
            return ChargeOutcome.RetryScheduled;
        }

        if (result.Success)
        {
            await SettleAsync(payment, result, resultEventId, inboxBusinessKey, cancellationToken);
            return ChargeOutcome.Settled;
        }

        var exhausted = payment.AttemptCount >= PaymentPolicy.MaxGatewayAttempts;
        if (result.Retryable && !exhausted)
        {
            payment.MarkPendingRetry(result.Code, result.Message);
            await SaveOrLoseAsync(cancellationToken);
            return ChargeOutcome.RetryScheduled;
        }

        await FailAsync(payment, result.Code, result.Message, exhausted, inboxBusinessKey, cancellationToken);
        return ChargeOutcome.Failed;
    }

    /// <summary>
    /// Claim nguyên tử: MarkCharging đổi Status thật nên EF phát sinh
    /// UPDATE ... WHERE "Id" = @id AND "Status" = @orig. Bên thua nhận
    /// DbUpdateConcurrencyException và phải đứng ngoài, tuyệt đối không gọi gateway.
    /// </summary>
    private async Task<bool> TryClaimAsync(PaymentEntity payment, CancellationToken cancellationToken)
    {
        try
        {
            payment.MarkCharging();
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogInformation("Mất claim cho payment {PaymentId} — có phiên khác đang charge.", payment.Id);
            _db.ChangeTracker.Clear();
            return false;
        }
        catch (InvalidPaymentTransitionException)
        {
            _db.Entry(payment).State = EntityState.Detached;
            return false;
        }
    }

    private async Task SettleAsync(
        PaymentEntity payment,
        GatewayChargeResult result,
        Guid settledEventId,
        string? inboxBusinessKey,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        payment.RecordGatewayAttempt(result);
        payment.MarkAuthorized();
        payment.MarkSettled(settledEventId);

        _publisher.EnqueuePaymentSettled(
            payment.Id, payment.TripId, payment.PaymentRequestKey, payment.RiderId,
            payment.Amount, payment.Currency, settledEventId,
            TryParseGuid(result.TransId), payment.CorrelationId);

        AddInboxRow(inboxBusinessKey, settledEventId);

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("Payment {PaymentId} settled cho TripId={TripId}, đã enqueue PaymentSettled.",
            payment.Id, payment.TripId);
    }

    /// <summary>
    /// Chốt nghĩa vụ thu tiền mặt. GatewayTransactionId = null vì không có giao dịch điện tử;
    /// TripService chỉ ghi nhận Paid, không đối soát transId với MoMo.
    /// </summary>
    private async Task SettleOfflineAsync(
        PaymentEntity payment,
        Guid settledEventId,
        string? inboxBusinessKey,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        payment.MarkCashCollected();
        payment.MarkSettled(settledEventId);

        _publisher.EnqueuePaymentSettled(
            payment.Id, payment.TripId, payment.PaymentRequestKey, payment.RiderId,
            payment.Amount, payment.Currency, settledEventId, gatewayTransactionId: null,
            payment.CorrelationId);

        AddInboxRow(inboxBusinessKey, settledEventId);

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Payment {PaymentId} ({Method}) thu offline cho TripId={TripId}, đã enqueue PaymentSettled.",
            payment.Id, payment.Method, payment.TripId);
    }

    private async Task FailAsync(
        PaymentEntity payment,
        string code,
        string message,
        bool retryExhausted,
        string? inboxBusinessKey,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        payment.MarkFailed(code, message);
        // Cùng một event id cho cả outbox message lẫn inbox claim: MessageId của inbox phải
        // trỏ tới đúng event đã phát, không phải một Guid rời rạc.
        var failedEventId = Guid.NewGuid();
        _publisher.EnqueuePaymentFailed(
            payment.Id, payment.TripId, payment.PaymentRequestKey, code, retryExhausted,
            payment.AttemptCount, failedEventId, payment.CorrelationId);
        AddInboxRow(inboxBusinessKey, failedEventId);

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogWarning("Payment {PaymentId} FAILED ({Code}, exhausted={Exhausted}), đã enqueue PaymentFailed.",
            payment.Id, code, retryExhausted);
    }

    /// <summary>Ghi kết quả trung gian; nếu mất race thì để phiên thắng ghi, không xung đột tiếp.</summary>
    private async Task SaveOrLoseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Phát hiện payload mâu thuẫn với nghĩa vụ đã chốt (cùng key nhưng khác amount/currency/method).
    /// Bắt buộc throw: exception đi qua pipeline của consumer nên message rơi vào _error kèm audit
    /// log — tuyệt đối không ACK im lặng, vì ACK nghĩa là TripService tin rằng tiền đã được thu
    /// theo số liệu sai. Cột PaymentRequestKey chỉ dài 128 nên key luôn nằm trong BusinessKey
    /// của inbox row; không cần cột payload hash mới để tái dựng xung đột lúc đối soát.
    /// </summary>
    private PaymentEntity VerifyOrThrow(PaymentEntity existing, ChargeRequest request)
    {
        var conflicts = new List<string>();

        if (existing.Amount != request.Amount)
            conflicts.Add($"Amount đã chốt {existing.Amount} ≠ event báo {request.Amount}");

        if (!string.Equals(existing.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            conflicts.Add($"Currency đã chốt {existing.Currency} ≠ event báo {request.Currency}");

        // Method chỉ là kênh thu (không phải số tiền), và rider đổi method qua REST sau dropoff
        // là hành vi có chủ đích của P1-1 — nên sự khác biệt ở đây là WARNING, không phải conflict.
        if (existing.Method != request.Method)
            _logger.LogInformation(
                "RideCompleted báo method {EventMethod} cho payment {PaymentId} đang giữ {PaymentMethod} " +
                "— tôn trọng method đã chốt trên nghĩa vụ.",
                request.Method, existing.Id, existing.Method);

        if (conflicts.Count > 0)
        {
            var detail = string.Join("; ", conflicts);
            _logger.LogError(
                "Xung đột idempotency ({Code}) trên obligation {PaymentRequestKey} (payment {PaymentId}): {Detail}. " +
                "Message sẽ rơi vào _error; không sửa giá trị đã chốt.",
                PaymentPolicy.ObligationConflictCode, request.PaymentRequestKey, existing.Id, detail);

            throw new InvalidOperationException(
                $"RideCompleted mâu thuẫn dữ liệu [{PaymentPolicy.ObligationConflictCode}] " +
                $"trên obligation {existing.PaymentRequestKey}: {detail}.");
        }

        return existing;
    }

    private void AddInboxRow(string? businessKey, Guid messageId)
    {
        if (businessKey is null)
            return;

        if (_db.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!_db.ProcessedEvents.Any(e => e.ConsumerName == InboxConsumer && e.BusinessKey == businessKey))
            {
                _db.ProcessedEvents.Add(new ProcessedEvent
                {
                    ConsumerName = InboxConsumer,
                    BusinessKey = businessKey,
                    MessageId = messageId
                });
            }
            return;
        }

        // INSERT thủ công: retry thủ công (Failed -> retry -> Settled) ghi lại CÙNG business key,
        // nên phải là upsert — Add sẽ biến conflict thành 500.
        _db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "ProcessedEvents" ("ConsumerName", "BusinessKey", "MessageId", "ProcessedAtUtc")
            VALUES ({0}, {1}, {2}, {3})
            ON CONFLICT ("ConsumerName", "BusinessKey") DO NOTHING
            """,
            [InboxConsumer, businessKey, messageId, DateTime.UtcNow]);
    }

    /// <summary>
    /// Claim inbox cho một kết quả đã chốt (upsert — xem AddInboxRow). SaveChanges là no-op
    /// trên Npgsql vì raw INSERT đã ghi, nhưng cần cho path InMemory của unit test.
    /// </summary>
    public async Task ClaimInboxAsync(string? businessKey, Guid messageId, CancellationToken cancellationToken)
    {
        if (businessKey is null)
            return;

        AddInboxRow(businessKey, messageId);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Ánh xạ domain→application. Exhaustive: thêm thành viên vào <see cref="PaymentMethod"/>
    /// mà quên xử lý ở đây sẽ không compile được (CS8503), thay vì âm thầm rơi vào default
    /// và charge nhầm qua MoMoApp — lỗi hardcode mà P1-1 sinh ra để diệt.
    /// Cash không có nhánh gateway: nghĩa vụ chốt ở <see cref="SettleOfflineAsync"/> và
    /// BuildIntent không bao giờ được gọi với method này.
    /// </summary>
    private static PaymentIntent BuildIntent(PaymentEntity payment) => new(
        payment.Id, payment.TripId, payment.PaymentRequestKey, payment.Amount, payment.Currency,
        payment.Method switch
        {
            PaymentMethod.MoMoWallet => PaymentMethodKind.MoMoWallet,
            PaymentMethod.MoMoApp => PaymentMethodKind.MoMoApp,
            PaymentMethod.Cash => throw new InvalidOperationException(
                $"Payment {payment.Id} là thu tiền mặt — không được gọi cổng điện tử."),
            _ => throw new InvalidOperationException(
                $"Phương thức thanh toán không được hỗ trợ: {(short)payment.Method}.")
        });

    private static Guid? TryParseGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : null;
}
