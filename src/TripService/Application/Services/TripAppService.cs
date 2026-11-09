using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SwiftRide.Contracts.Events;
using TripService.Application.Abstractions;
using TripService.Application.DTOs;
using TripService.Domain;
using TripService.Domain.Entities;
using TripService.Domain.Services;
using TripService.Infrastructure.Payment;
using TripService.Infrastructure.Persistence;

namespace TripService.Application.Services;

public sealed class TripAppService : ITripAppService
{
    private readonly TripDbContext _db;
    private readonly IFareCalculator _fareCalculator;
    private readonly ITripEventPublisher _publisher;
    private readonly IPaymentReadModelClient _paymentReadModel;
    private readonly ILogger<TripAppService> _logger;

    public TripAppService(
        TripDbContext db,
        IFareCalculator fareCalculator,
        ITripEventPublisher publisher,
        IPaymentReadModelClient paymentReadModel,
        ILogger<TripAppService> logger)
    {
        _db = db;
        _fareCalculator = fareCalculator;
        _publisher = publisher;
        _paymentReadModel = paymentReadModel;
        _logger = logger;
    }

    public async Task<TripResponse> CreateTripAsync(Guid riderId, CreateTripRequest request,
        CancellationToken cancellationToken = default)
    {
        var fare = _fareCalculator.Calculate(request.PickupLatitude, request.PickupLongitude,
            request.DestinationLatitude, request.DestinationLongitude);

        var trip = Trip.Create(riderId, request.PickupAddress, request.PickupLatitude, request.PickupLongitude,
            request.DestinationAddress, request.DestinationLatitude, request.DestinationLongitude, fare);

        _db.Trips.Add(trip);
        await SaveWithHistoryAsync(trip, TripStatus.Requested, "Hành khách tạo chuyến", riderId, "Rider", null,
            cancellationToken);

        return TripResponse.From(trip);
    }

    public Task<TripResponse> AssignDriverAsync(Guid tripId, Guid driverId, CancellationToken cancellationToken = default) =>
        TransitionAsync(tripId, trip =>
        {
            trip.AssignDriver(driverId);
            return (TripStatus.DriverAccepted, "Tài xế nhận chuyến", driverId, "Driver");
        }, cancellationToken);

    public Task<TripResponse> MarkEnRouteAsync(Guid tripId, Guid driverId, CancellationToken cancellationToken = default) =>
        TransitionAsync(tripId, trip =>
        {
            trip.MarkEnRoute(driverId);
            return (TripStatus.EnRoute, "Tài xế đang tới điểm đón", driverId, "Driver");
        }, cancellationToken);

    public Task<TripResponse> MarkPickedUpAsync(Guid tripId, Guid driverId, CancellationToken cancellationToken = default) =>
        TransitionAsync(tripId, trip =>
        {
            trip.MarkPickedUp(driverId);
            return (TripStatus.PickedUp, "Đã đón khách", driverId, "Driver");
        }, cancellationToken);

    public async Task<TripResponse> MarkDroppedOffAsync(Guid tripId, Guid driverId,
        RidePaymentMethod? paymentMethod = null, CancellationToken cancellationToken = default)
    {
        var trip = await GetForUpdateAsync(tripId, cancellationToken);
        var from = trip.Status;

        // Rider có thể đã chọn method trước dropoff (P1-1); driver không được ghi đè lựa chọn đó.
        var method = paymentMethod ?? await GetStoredMethodAsync(trip.Id, cancellationToken)
            ?? RidePaymentMethod.MoMoApp;

        trip.MarkDroppedOff(driverId, out var eventId);

        // Hai bước nghiệp vụ trong một lần lưu: DroppedOff rồi PaymentPending.
        AddHistory(trip, from, TripStatus.DroppedOff, driverId, "Driver", "Tài xế trả khách", eventId);
        AddHistory(trip, TripStatus.DroppedOff, TripStatus.PaymentPending, driverId, "Driver",
            "Chờ thanh toán từ PaymentService", eventId);

        // Xóa bản nháp method (nếu có) để dispatcher chỉ gửi đúng event thật vừa mint.
        _publisher.DropPendingRideCompleted(trip.Id);

        // Key luôn được mint ở MarkDroppedOff phía trên; null chỉ xảy ra nếu domain đổi hợp đồng.
        if (trip.PaymentRequestKey is null)
            throw new InvalidTripTransitionException($"Trip {trip.Id} chưa có PaymentRequestKey ở dropoff.");

        _publisher.EnqueueRideCompleted(
            trip.Id, trip.RiderId, trip.DriverId, trip.EstimatedPrice, trip.Currency,
            trip.PaymentRequestKey!, eventId, Activity.Current?.TraceId.ToString(), method);

        await CommitAsync(cancellationToken);
        _logger.LogInformation("Trip {TripId} chuyển PaymentPending, đã enqueue RideCompleted event={EventId}.",
            trip.Id, eventId);

        return TripResponse.From(trip) with { PaymentMethod = method.ToString() };
    }

    public async Task<TripResponse> SetPaymentMethodAsync(Guid tripId, Guid riderId,
        RidePaymentMethod paymentMethod, CancellationToken cancellationToken = default)
    {
        var trip = await GetForUpdateAsync(tripId, cancellationToken);

        if (trip.RiderId != riderId)
            throw new UnauthorizedAccessException("Chỉ hành khách của chuyến mới chọn được phương thức thanh toán.");

        // Paid/Cancelled/Failed: nghĩa vụ đã chốt — không còn gì để đổi.
        if (trip.Status is TripStatus.Paid or TripStatus.Cancelled or TripStatus.Failed)
            throw new InvalidTripTransitionException(
                $"Trip đã ở trạng thái {trip.Status}, không đổi được phương thức thanh toán.");

        // TripDB không có cột riêng cho method (chỉ EnsureCreated), nên lựa chọn của rider
        // được lưu ngay trong intent RideCompleted chưa gửi của chính trip.
        if (trip.Status == TripStatus.PaymentPending)
        {
            if (trip.PaymentRequestKey is null)
            {
                // Không thể có thật (PaymentPending luôn đã mint key); thà báo rõ còn hơn gửi
                // payload rỗng key.
                throw new InvalidTripTransitionException(
                    $"Trip {trip.Id} ở PaymentPending nhưng chưa có PaymentRequestKey.");
            }

            // Intent cũ còn nằm trong outbox thì thay tại chỗ; đã lên broker rồi thì republish
            // với EventId mới. PaymentService giữ method đã chốt trên nghĩa vụ, nên nếu rider
            // chậm hơn consumer, đường chuyên dụng vẫn là PATCH /payments/{id}/method.
            var wasDispatched = _publisher.CountPendingRideCompleted(trip.Id) == 0;
            _publisher.DropPendingRideCompleted(trip.Id);
            EnqueueRideCompletedForTrip(trip, paymentMethod);

            if (wasDispatched)
                _logger.LogInformation(
                    "Trip {TripId} republish RideCompleted với method {Method} (intent cũ đã gửi).",
                    trip.Id, paymentMethod);
        }
        else
        {
            // Chưa dropoff: chỉ có bản nháp method, sẽ bị thay bằng event thật ở lần dropoff.
            StageMethod(trip.Id, paymentMethod);
        }

        await CommitAsync(cancellationToken);
        return TripResponse.From(trip) with { PaymentMethod = paymentMethod.ToString() };
    }

    public async Task<RidePaymentMethod?> GetPaymentMethodAsync(
        Guid tripId, Guid actorId, bool canViewAll, CancellationToken cancellationToken = default)
    {
        // TripDB không lưu method thành cột, nên vẫn phải đọc trip để kiểm tra tư cách tham gia
        // trước khi lộ lựa chọn thanh toán cho người không liên quan.
        var trip = await _db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken)
            ?? throw new KeyNotFoundException($"Không tìm thấy trip {tripId}.");

        if (!canViewAll && trip.RiderId != actorId && trip.DriverId != actorId)
            throw new UnauthorizedAccessException("Bạn không phải người tham gia chuyến này.");

        return await GetStoredMethodAsync(tripId, cancellationToken);
    }

    /// <summary>Key dự phòng cho intent nháp — cố ý khác quy ước trip: nếu dispatcher kịp gửi
    /// trước khi dropoff xóa nháp, consumer phía PaymentService sẽ từ chối key không khớp và
    /// message rơi vào _error (không bao giờ tạo nghĩa vụ thanh toán sai). Cửa sổ này nhỏ hơn
    /// nhiều lần so với một chuyến đi, và TripDB không có cột riêng để lưu method.</summary>
    private const string StagedRequestKeyPrefix = "trip-staged:";

    /// <summary>Intent nháp chưa phải payload thật; currency chỉ cần thỏa ràng buộc độ dài cột.</summary>
    private const string CurrencyPlaceholder = "VND";

    /// <summary>
    /// Method rider đã chọn, suy ra từ intent RideCompleted chưa gửi của trip (outbox là nguồn
    /// duy nhất vì TripDB không có cột riêng). null khi chưa chọn hoặc dispatcher đã gửi hết.
    /// </summary>
    private Task<RidePaymentMethod?> GetStoredMethodAsync(Guid tripId, CancellationToken cancellationToken)
        => Task.FromResult(_publisher.PeekPendingPaymentMethod(tripId));

    /// <summary>Chưa dropoff: ghi method vào intent nháp, bị xóa khi dropoff mint event thật.</summary>
    private void StageMethod(Guid tripId, RidePaymentMethod method) =>
        _publisher.EnqueueRideCompleted(tripId, Guid.Empty, null, 0m, CurrencyPlaceholder,
            StagedRequestKeyPrefix + tripId.ToString("N"), Guid.NewGuid(), null, method);

    private void EnqueueRideCompletedForTrip(Trip trip, RidePaymentMethod method) =>
        _publisher.EnqueueRideCompleted(
            trip.Id, trip.RiderId, trip.DriverId, trip.EstimatedPrice, trip.Currency,
            trip.PaymentRequestKey!, Guid.NewGuid(), Activity.Current?.TraceId.ToString(), method);

    public async Task<TripResponse> CancelTripAsync(Guid tripId, Guid actorId, bool actorIsRider, string? reason,
        CancellationToken cancellationToken = default)
    {
        var trip = await GetForUpdateAsync(tripId, cancellationToken);
        var from = trip.Status;

        trip.Cancel(actorId, actorIsRider);
        await SaveWithHistoryAsync(trip, from, reason ?? "Hủy chuyến", actorId,
            actorIsRider ? "Rider" : "System", null, cancellationToken);

        return TripResponse.From(trip);
    }

    public async Task<TripResponse?> GetTripAsync(Guid tripId, Guid actorId, bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        var trip = await _db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken);
        if (trip is null)
            return null;

        if (!canViewAll && trip.RiderId != actorId && trip.DriverId != actorId)
            throw new UnauthorizedAccessException("Bạn không phải người tham gia chuyến này.");

        return await WithPaymentSnapshotAsync(
            TripResponse.From(trip) with
            {
                // P1-1: rider thấy được method mình đã chọn khi trip còn chờ thanh toán.
                PaymentMethod = (await GetStoredMethodAsync(trip.Id, cancellationToken))?.ToString()
            },
            trip, cancellationToken);
    }

    public async Task<IReadOnlyList<TripResponse>> ListTripsForAdminAsync(
        string? status, DateTime? from, DateTime? to, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Trips.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<TripStatus>(status, ignoreCase: true, out var parsed))
                throw new ArgumentException($"Trạng thái '{status}' không hợp lệ.");
            query = query.Where(t => t.Status == parsed);
        }

        if (from is not null) query = query.Where(t => t.CreatedAtUtc >= from.Value);
        if (to is not null) query = query.Where(t => t.CreatedAtUtc <= to.Value);

        var items = await query
            .OrderByDescending(t => t.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // List chỉ gắn nhãn suy ra từ TripDB (miễn phí). Đọc snapshot PaymentService theo
        // từng dòng = N request cho một trang admin — chi phí không xứng, detail đã có.
        return items
            .Select(t => t.Status == TripStatus.Paid
                ? TripResponse.From(t).AsPaidSettled()
                : TripResponse.From(t))
            .ToList();
    }

    /// <summary>
    /// P1-2: rider bấm "thử lại thanh toán" trên trip của mình. Toàn bộ việc charge nằm ở
    /// PaymentService; TripService chỉ kiểm tra tư cách tham gia + nghĩa vụ đã mint chưa,
    /// rồi trả về snapshot mới nhất (PaymentPending → nhãn vừa cập nhật, Paid → "Paid").
    /// Chỉ chủ nợ (RiderId) được retry; actorIsRider = false là đường admin — PaymentService
    /// vẫn tự chặn người không phải chủ payment, nên admin muốn retry hộ phải dùng API payment.
    /// </summary>
    public async Task<PaymentRetryResult> RetryPaymentAsync(Guid tripId, Guid actorId, bool actorIsRider,
        CancellationToken cancellationToken = default)
    {
        var trip = await _db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken)
            ?? throw new KeyNotFoundException($"Không tìm thấy trip {tripId}.");

        if (trip.RiderId != actorId)
            throw new UnauthorizedAccessException(
                actorIsRider
                    ? "Chỉ hành khách của chuyến mới retry được thanh toán."
                    : "Không thể retry thanh toán cho trip của hành khách khác.");

        if (trip.Status is TripStatus.Cancelled or TripStatus.Failed)
            return new PaymentRetryResult(PaymentRetryStatus.Rejected, TripResponse.From(trip),
                $"Trip đã ở trạng thái {trip.Status}, không retry được thanh toán.");

        // Paid: tiền đã chốt — gọi retry chỉ tổ reset hạn mức retry trên một nghĩa vụ đã xong.
        if (trip.Status == TripStatus.Paid)
            return new PaymentRetryResult(PaymentRetryStatus.Rejected,
                TripResponse.From(trip).AsPaidSettled(), "Trip đã thanh toán, không cần thử lại.");

        // Requested..DroppedOff: chưa dropoff thì chưa mint nghĩa vụ, retry sẽ chỉ là 404 phía
        // PaymentService — chặn ngay ở đây để rider nhận thông điệp có nghĩa.
        if (trip.Status != TripStatus.PaymentPending)
            return new PaymentRetryResult(PaymentRetryStatus.Rejected, TripResponse.From(trip),
                $"Trip đang ở trạng thái {trip.Status}, chưa có nghĩa vụ thanh toán để retry.");

        var outcome = await _paymentReadModel.RetryForTripAsync(trip.Id, cancellationToken);

        // Ưu tiên hiện trạng PaymentService vừa trả về; nếu nó im lặng (read model tắt/lỗi)
        // thì rơi về một lần đọc best-effort. WithPaymentSnapshot(null) trên trip Paid vẫn suy
        // ra "Paid", nên retry hỏng không làm mất nhãn thanh toán của trip.
        var response = TripResponse.From(trip).WithPaymentSnapshot(outcome.Snapshot is null
            ? null
            : new PaymentSnapshotDto(
                outcome.Snapshot.PaymentId, outcome.Snapshot.Status,
                outcome.Snapshot.FailureCode, outcome.Snapshot.AttemptCount));

        if (outcome.Snapshot is null)
        {
            response = await WithPaymentSnapshotAsync(response, trip, cancellationToken);
        }

        // PaymentService chỉ retry được payment ở Failed; nghĩa vụ còn Created/PendingRetry/
        // Charging nghĩa là worker vẫn tự xử lý → 202 "đang chờ", đừng báo lỗi cho rider.
        var status = outcome.Success
            ? PaymentRetryStatus.Retrying
            : IsObligationStillInFlight(response.PaymentStatus)
                ? PaymentRetryStatus.AcceptedNoAction
                : PaymentRetryStatus.Rejected;

        var message = status switch
        {
            PaymentRetryStatus.Retrying => "Đã gửi yêu cầu thử lại thanh toán.",
            PaymentRetryStatus.AcceptedNoAction =>
                outcome.FailureReason ?? "Nghĩa vụ thanh toán vẫn đang được xử lý tự động, vui lòng chờ.",
            _ => outcome.FailureReason ?? "PaymentService không nhận yêu cầu retry."
        };

        return new PaymentRetryResult(status, response, message);
    }

    /// <summary>Nhãn read model khi nghĩa vụ chưa chốt và chưa Failed — worker còn đang lo.</summary>
    private static bool IsObligationStillInFlight(string? paymentStatus) =>
        paymentStatus is "Processing" or "Unknown";

    private async Task<TripResponse> TransitionAsync(
        Guid tripId,
        Func<Trip, (TripStatus To, string Reason, Guid Actor, string ActorType)> action,
        CancellationToken cancellationToken)
    {
        var trip = await GetForUpdateAsync(tripId, cancellationToken);
        var from = trip.Status;
        var result = action(trip);

        await SaveWithHistoryAsync(trip, from, result.Reason, result.Actor, result.ActorType, null, cancellationToken);
        return TripResponse.From(trip);
    }

    private async Task<Trip> GetForUpdateAsync(Guid tripId, CancellationToken cancellationToken)
    {
        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken)
            ?? throw new KeyNotFoundException($"Không tìm thấy trip {tripId}.");
        return trip;
    }

    private async Task SaveWithHistoryAsync(
        Trip trip, TripStatus from, string? reason, Guid? actorId, string actorType, Guid? eventId,
        CancellationToken cancellationToken)
    {
        AddHistory(trip, from, trip.Status, actorId, actorType, reason, eventId);
        await CommitAsync(cancellationToken);
    }

    private void AddHistory(Trip trip, TripStatus from, TripStatus to, Guid? actorId, string actorType,
        string? reason, Guid? eventId)
    {
        _db.TripStatusHistories.Add(new TripStatusHistory
        {
            TripId = trip.Id,
            FromStatus = from,
            ToStatus = to,
            ActorId = actorId,
            ActorType = actorType,
            Reason = reason,
            EventId = eventId
        });
    }

    private async Task CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidTripTransitionException(
                "Chuyến đi vừa được cập nhật bởi yêu cầu khác, vui lòng thử lại.");
        }
    }

    /// <summary>
    /// Read model P1-4: trip kẹt PaymentPending cần cho rider thấy lý do (failure code,
    /// retry count) và payment id để retry. Trip đã Paid chốt ngay nhãn "Paid" từ TripStatus
    /// — không gọi PaymentService, nên field này vẫn có kể cả khi read model tắt.
    /// Client best-effort nên lỗi network chỉ làm thiếu field, không hỏng API.
    /// </summary>
    private async Task<TripResponse> WithPaymentSnapshotAsync(
        TripResponse response, Trip trip, CancellationToken cancellationToken)
    {
        if (trip.Status == TripStatus.Paid)
            return response.AsPaidSettled();

        if (trip.Status != TripStatus.PaymentPending)
            return response;

        var snapshot = await _paymentReadModel.TryGetForTripAsync(trip.Id, cancellationToken);
        if (snapshot is null)
            return response;

        return response.WithPaymentSnapshot(new PaymentSnapshotDto(
            snapshot.PaymentId, snapshot.Status, snapshot.FailureCode, snapshot.AttemptCount));
    }
}
