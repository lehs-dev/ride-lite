using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TripService.Application.Abstractions;
using TripService.Domain.Entities;
using TripService.Infrastructure.Persistence;
using SwiftRide.Contracts.Events;

namespace TripService.Infrastructure.Messaging;

/// <summary>Ghi intent RideCompleted vào outbox trong chính transaction đang mở của caller.</summary>
public sealed class TripEventPublisher : ITripEventPublisher
{
    private readonly TripDbContext _db;

    public TripEventPublisher(TripDbContext db)
    {
        _db = db;
    }

    public void EnqueueRideCompleted(
        Guid tripId,
        Guid riderId,
        Guid? driverId,
        decimal amount,
        string currency,
        string paymentRequestKey,
        Guid eventId,
        string? correlationId,
        RidePaymentMethod paymentMethod = RidePaymentMethod.MoMoApp)
    {
        var message = new RideCompleted(
            EventId: eventId,
            TripId: tripId,
            RiderId: riderId,
            DriverId: driverId,
            Amount: amount,
            Currency: currency,
            OccurredAtUtc: DateTime.UtcNow,
            CorrelationId: ParseCorrelation(correlationId),
            PaymentRequestKey: paymentRequestKey,
            PaymentMethod: paymentMethod);

        _db.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = eventId,
            EventType = nameof(RideCompleted),
            AggregateId = tripId,
            // Web defaults (camelCase) khớp serializer mặc định của MassTransit/RabbitMQ,
            // và là options mà OutboxDispatcherWorker dùng khi đọc lại payload.
            Payload = JsonSerializer.Serialize(message, typeof(RideCompleted), PayloadOptions)
        });
    }

    /// <summary>Web defaults (camelCase) — cùng options mà OutboxDispatcherWorker dùng khi đọc lại payload.</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Đếm intent RideCompleted của trip còn nằm trong outbox (chưa lên broker), tính cả row
    /// Added chưa SaveChanges — SetPaymentMethod gọi liên tiếp Drop/Count trong một request.
    /// </summary>
    public int CountPendingRideCompleted(Guid tripId) =>
        _db.OutboxMessages.Local.Count(m => m.EventType == nameof(RideCompleted)
            && m.AggregateId == tripId && m.DeliveredAtUtc == null
            && _db.Entry(m).State != EntityState.Deleted)
        + _db.OutboxMessages.Count(m => m.EventType == nameof(RideCompleted)
            && m.AggregateId == tripId && m.DeliveredAtUtc == null);

    /// <summary>
    /// Xóa intent RideCompleted chưa gửi của trip. Query DB (bắt cả row đã commit) hợp nhất
    /// với local entries (row Added chưa SaveChanges), bỏ qua row đang ở state Deleted.
    /// </summary>
    public void DropPendingRideCompleted(Guid tripId)
    {
        var fromDb = _db.OutboxMessages
            .Local.Where(m => m.EventType == nameof(RideCompleted)
                && m.AggregateId == tripId && m.DeliveredAtUtc == null
                && _db.Entry(m).State != EntityState.Deleted)
            .ToList();

        var fromQuery = _db.OutboxMessages
            .Where(m => m.EventType == nameof(RideCompleted) && m.AggregateId == tripId && m.DeliveredAtUtc == null)
            .ToList();

        _db.OutboxMessages.RemoveRange(fromDb.Concat(fromQuery).Distinct());
    }

    /// <summary>
    /// Method trong intent RideCompleted chưa gửi gần nhất của trip. Đọc local + DB vì bản nháp
    /// rider vừa ghi có thể chưa SaveChanges. Payload hỏng → bỏ qua (null) chứ không hỏng API.
    /// </summary>
    public RidePaymentMethod? PeekPendingPaymentMethod(Guid tripId)
    {
        var candidates = _db.OutboxMessages.Local
            .Where(m => m.EventType == nameof(RideCompleted) && m.AggregateId == tripId
                && m.DeliveredAtUtc == null && _db.Entry(m).State != EntityState.Deleted)
            .ToList();

        candidates.AddRange(_db.OutboxMessages
            .Where(m => m.EventType == nameof(RideCompleted) && m.AggregateId == tripId && m.DeliveredAtUtc == null)
            .AsNoTracking()
            .ToList());

        foreach (var payload in candidates.DistinctBy(m => m.Id).OrderByDescending(m => m.Id)
                     .Select(m => m.Payload))
        {
            // Đọc bằng đúng serializer ghi payload; parse tay sẽ vỡ khi enum đổi sang dạng chuỗi.
            RideCompleted? message;
            try
            {
                message = JsonSerializer.Deserialize<RideCompleted>(payload, PayloadOptions);
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"Payload RideCompleted của trip {tripId} không đọc được: {ex.Message}");
                continue;
            }

            if (message is not null && Enum.IsDefined(message.PaymentMethod))
                return message.PaymentMethod;
        }

        return null;
    }

    /// <summary>W3C TraceId (hex 32) khác định dạng Guid N — parse mềm, không throw ở hot path.</summary>
    private static Guid? ParseCorrelation(string? value)
    {
        if (Guid.TryParse(value, out var parsed))
            return parsed;

        if (value is { Length: 32 })
        {
            try
            {
                return Guid.ParseExact(value, "N");
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                Debug.WriteLine($"Không parse được CorrelationId '{value}': {ex.Message}");
            }
        }

        return null;
    }
}
