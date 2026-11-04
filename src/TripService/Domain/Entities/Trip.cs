using TripService.Domain.Enums;
using TripService.Domain.States;
namespace TripService.Domain.Entities;

    public class Trip
    {
        public Guid Id {get; set;}  = Guid.NewGuid();
        public Guid RiderId {get; set;}
        public Guid? DriverId { get; private set; }

        public TripStatus Status { get; private set; }
            = TripStatus.Requested;
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
        private TripState GetCurrentState()
        {
            return Status switch
            {
                TripStatus.Requested => new RequestedState(),
                TripStatus.DriverAccepted => new DriverAcceptedState(),

                _ => throw new InvalidOperationException(
                    $"Chưa hỗ trợ xử lý trạng thái {Status}.")
            };
        }
        public void AcceptDriver(Guid driverId)
        {
            if (driverId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Mã tài xế không hợp lệ.",
                    nameof(driverId));
            }

            if (driverId == RiderId)
            {
                throw new InvalidOperationException(
                    "Hành khách không thể tự nhận chuyến của mình.");
            }

            var currentState = GetCurrentState();
            var nextState = currentState.Accept();

            DriverId = driverId;
            Status = nextState.Status;
            UpdatedAtUtc = DateTime.UtcNow;
        }

    }