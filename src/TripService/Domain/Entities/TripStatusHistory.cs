namespace TripService.Domain.Entities;

/// <summary>Lịch sử chuyển trạng thái, ghi append-only cùng transaction với thay đổi (M5-04).</summary>
public class TripStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip? Trip { get; set; }
    public TripStatus FromStatus { get; set; }
    public TripStatus ToStatus { get; set; }
    public Guid? ActorId { get; set; }

    /// <summary>Rider | Driver | System | Consumer</summary>
    public string ActorType { get; set; } = "System";

    public string? Reason { get; set; }
    public Guid? EventId { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
