namespace PaymentService.Domain.Entities;

/// <summary>
/// Outbox intent: ghi cùng transaction với nghiệp vụ, dispatcher gửi rồi mới đánh dấu.
/// MessageId giữ nguyên qua mọi lần thử lại để consumer phía Trip dedup được.
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public Guid MessageId { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = string.Empty;
    public Guid AggregateId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAtUtc { get; set; }
    public int DeliveryAttempts { get; set; }
    public string? LastError { get; set; }
}
