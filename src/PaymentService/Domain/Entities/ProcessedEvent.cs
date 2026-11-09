namespace PaymentService.Domain.Entities;

/// <summary>
/// Inbox dedup theo khóa nghiệp vụ (ConsumerName, BusinessKey). PK chặn redelivery
/// tạo nghiệp vụ lặp — docs/EventContract.md §3.4 yêu cầu không chỉ dựa vào EventId.
/// </summary>
public class ProcessedEvent
{
    public string ConsumerName { get; set; } = string.Empty;
    public string BusinessKey { get; set; } = string.Empty;
    public Guid? MessageId { get; set; }
    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
}
