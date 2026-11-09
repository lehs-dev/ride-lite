using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Contracts.Events;
using TripService.Domain.Entities;
using TripService.Infrastructure.Persistence;

namespace TripService.Infrastructure.Messaging;

/// <summary>
/// Gửi outbox → RabbitMQ với UPDLOCK/READPAST để nhiều instance không giành cùng một row.
/// Broker sập thì rows tích lại và service vẫn phục vụ HTTP bình thường.
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
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();

        // UPDLOCK/READPAST chỉ vô nghĩa nếu lock được giữ suốt chu kỳ select -> publish ->
        // đánh dấu delivered. Với autocommit, lock nhả ngay sau SELECT và SaveChanges là
        // transaction khác, nên hai instance cùng đọc và cùng publish một row.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // SQL Server không có SKIP LOCKED — dùng WITH (UPDLOCK, READPAST).
        var pending = await db.OutboxMessages
            .FromSqlRaw(
                """
                SELECT TOP (@p0) * FROM [TripOutboxMessages] WITH (UPDLOCK, READPAST)
                WHERE [DeliveredAtUtc] IS NULL
                ORDER BY [Id]
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
                // Broker sập: đếm attempt nhưng không bao giờ bỏ mất intent.
                RecordFailure(message, ex);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = Deserialize(message.EventType, message.Payload);
        // IPublishEndpoint.Publish(object, Type, Action<IPublishContext>, CancellationToken):
        // giữ nguyên MessageId qua mọi lần thử lại để consumer phía Payment dedup được.
        await _bus.Publish(payload, payload.GetType(),
            ctx => ctx.MessageId = message.MessageId, cancellationToken);
    }

    private void RecordFailure(OutboxMessage message, Exception exception)
    {
        message.DeliveryAttempts++;
        var text = exception.Message;
        message.LastError = text.Length > 1024 ? text[..1024] : text;
        _logger.LogWarning(exception, "Gửi outbox {MessageId} ({EventType}) thất bại lần {Attempt}.",
            message.MessageId, message.EventType, message.DeliveryAttempts);
    }

    private static object Deserialize(string eventType, string payload) => eventType switch
    {
        nameof(RideCompleted) => JsonSerializer.Deserialize<RideCompleted>(payload, JsonOptions)
            ?? throw new InvalidOperationException($"Payload {nameof(RideCompleted)} rỗng."),
        _ => throw new NotSupportedException($"EventType {eventType} chưa được hỗ trợ.")
    };
}
