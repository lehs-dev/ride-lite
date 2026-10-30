namespace TripService.Domain.Entities;
using TripService.Domain.Enums;
    public class Trip
    {
        public Guid Id {get; set;}  = Guid.NewGuid();
        public Guid RiderId {get; set;}
        public Guid? DriverId {get; set;}
        public TripStatus Status { get; set; } = TripStatus.Requested;
        public double FromLat { get; set; }
        public double FromLon { get; set; }
        public string FromAddress { get; set; } = string.Empty;
        public double ToLat { get; set; }
        public double ToLon { get; set; }
        public string ToAddress { get; set; } = string.Empty;
        public double? Distance { get; set; }
        public double? EstimatedTime { get; set; }
        public decimal? Price { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? PickUpTimeUtc { get; set; }
        public DateTime? DropOffTimeUtc { get; set; }
        public string PaymentMethod { get; set; } = string.Empty;
        public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;
        

    }