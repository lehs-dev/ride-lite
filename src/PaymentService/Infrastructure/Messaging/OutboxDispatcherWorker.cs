using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Persistence;
using SwiftRide.Contracts.Events;

namespace PaymentService.Infrastructure.Messaging;

/// <summary>
/// Gửi outbox → RabbitMQ. MessageId phát lại đúng giá trị lưu trong row để consumer
/// phía Trip dedup được kể cả khi dispatch thử lại nhiều lần.
/// </summary>
public sealed class OutboxDispatcherWorker : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBus _bus;
    private readonly ILogger<OutboxDispatcherWorker> _logger;

    public OutboxDispatcherWorker(
        IServiceScopeFactory scopeFactory,
        IBus bus,
        ILogger<OutboxDispatcherWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Broker chưa sẵn sàng: rows tích lại trong outbox, service vẫn phục vụ HTTP.
                _logger.LogWarning(ex, "Outbox dispatch lỗi chu kỳ, sẽ thử lại.");
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

    private async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        // FOR UPDATE SKIP LOCKED chỉ có tác dụng nếu lock được GIỮ suốt chu kỳ
        // select -> publish -> đánh dấu delivered. Với autocommit, lock nhả ngay sau SELECT
        // và SaveChanges là transaction khác, nên hai instance cùng đọc và cùng publish một row.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var pending = await db.OutboxMessages
            .FromSqlRaw(
                """
                SELECT * FROM "PaymentOutboxMessages"
                WHERE "DeliveredAtUtc" IS NULL
                ORDER BY "Id"
                LIMIT {0}
                FOR UPDATE SKIP LOCKED
                """,
                BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return;
        }

        foreach (var message in pending)
        {
            try
            {
                await PublishAsync(message, cancellationToken);
                message.DeliveredAtUtc = DateTime.UtcNow;
                message.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.DeliveryAttempts++;
                message.LastError = ex.Message.Length > 1024 ? ex.Message[..1024] : ex.Message;
                _logger.LogWarning(ex, "Không gửi được outbox {MessageId} ({EventType})",
                    message.MessageId, message.EventType);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = Deserialize(message.EventType, message.Payload);
        // IPublishEndpoint.Publish(object, Type, Action<IPublishContext>, CancellationToken):
        // giữ nguyên MessageId qua mọi lần thử lại để consumer phía Trip dedup được.
        await _bus.Publish(payload, payload.GetType(),
            ctx => ctx.MessageId = message.MessageId, cancellationToken);
    }

    private static object Deserialize(string eventType, string payload) => eventType switch
    {
        nameof(PaymentSettled) => JsonSerializer.Deserialize<PaymentSettled>(payload, JsonOptions)
            ?? throw new InvalidOperationException($"Payload {nameof(PaymentSettled)} rỗng."),
        nameof(PaymentFailed) => JsonSerializer.Deserialize<PaymentFailed>(payload, JsonOptions)
            ?? throw new InvalidOperationException($"Payload {nameof(PaymentFailed)} rỗng."),
        _ => throw new NotSupportedException($"EventType {eventType} chưa được hỗ trợ.")
    };
}
