using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentEntity = PaymentService.Domain.Entities.Payment;
using PaymentService.Application.Abstractions;
using PaymentService.Domain;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Payment;
using PaymentService.Infrastructure.Persistence;
using PaymentService.Infrastructure.Services;

namespace PaymentService.Infrastructure.Messaging;

/// <summary>
/// Hai việc nền, tất cả đi qua PaymentProcessor (không còn bản sao logic charge thứ ba):
/// 1. Recover: reclaim lease Charging quá hạn (process chết giữa gateway call và lần ghi kết quả)
///    và charge các nghĩa vụ Created bị mồ côi — đây là đường cứu duy nhất cho P0-1.
/// 2. Retry: đẩy PendingRetry tới trần MaxGatewayAttempts.
///
/// Mỗi item chạy trong scope riêng và có try/catch riêng: một row lỗi không được làm hỏng cả batch.
/// </summary>
public sealed class PaymentBackgroundWorker : BackgroundService
{
    private const int BatchSize = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentBackgroundWorker> _logger;

    public PaymentBackgroundWorker(IServiceScopeFactory scopeFactory, ILogger<PaymentBackgroundWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepPaymentsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Payment background cycle lỗi.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepPaymentsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var createdCut = now - PaymentPolicy.OrphanedCreatedGrace;
        var leaseCut = now - PaymentPolicy.ChargingLease;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        // Chỉ lấy ID: mỗi item sẽ tự đọc lại trong scope riêng của nó.
        var ids = await db.Payments
            .Where(p => p.Status == PaymentStatus.PendingRetry
                        || (p.Status == PaymentStatus.Created && p.CreatedAtUtc < createdCut)
                        || (p.Status == PaymentStatus.Charging && p.UpdatedAtUtc < leaseCut))
            .OrderBy(p => p.CreatedAtUtc)
            .Select(p => p.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            try
            {
                await ProcessOnePaymentAsync(id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Xử lý payment {PaymentId} trong worker thất bại, sẽ thử lại chu kỳ sau.", id);
            }
        }
    }

    private async Task ProcessOnePaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<PaymentProcessor>();

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null || payment.IsRideOutcomeFinal)
            return;

        // Lease quá hạn = phiên cũ có thể đã gọi gateway nhưng không ghi được kết quả.
        // Chuyển về PendingRetry bằng một UPDATE đổi Status (concurrency guard) rồi mới claim lại,
        // để không bao giờ có hai phiên cùng charge.
        if (payment.Status == PaymentStatus.Charging)
        {
            if (!payment.IsChargingLeaseExpired(DateTime.UtcNow))
                return;

            payment.MarkChargingLeaseExpired();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return; // phiên cũ vẫn còn sống và vừa ghi đè — để họ chốt.
            }
        }

        await processor.ChargeAsync(payment, Guid.NewGuid(), null, cancellationToken);
    }
}