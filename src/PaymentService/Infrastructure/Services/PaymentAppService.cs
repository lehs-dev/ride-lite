using Microsoft.EntityFrameworkCore;
using PaymentEntity = PaymentService.Domain.Entities.Payment;
using PaymentService.Application.DTOs;
using PaymentService.Application.Services;
using PaymentService.Domain;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Persistence;
using SwiftRide.Contracts.Events;

namespace PaymentService.Infrastructure.Services;

/// <summary>
/// Đường REST của luồng thanh toán. Không còn tự gọi gateway: mọi charge đi qua
/// PaymentProcessor — cùng một claim, cùng một quy tắc inbox với consumer và worker.
/// Trước đây đường REST là bản sao logic thứ hai, và đó là nguồn lỗi double-charge.
/// </summary>
public sealed class PaymentAppService : IPaymentAppService
{
    /// <summary>Trần pageSize của mọi endpoint phân trang — khớp Math.Clamp ở boundary.</summary>
    internal const int MaxPageSize = 100;

    private readonly PaymentDbContext _db;
    private readonly PaymentProcessor _processor;
    private readonly ILogger<PaymentAppService> _logger;

    public PaymentAppService(
        PaymentDbContext db,
        PaymentProcessor processor,
        ILogger<PaymentAppService> logger)
    {
        _db = db;
        _processor = processor;
        _logger = logger;
    }

    /// <summary>
    /// Đẩy tiếp nghĩa vụ thanh toán của một trip. Số tiền KHÔNG bao giờ đến từ client: nếu
    /// chưa có RideCompleted thì chưa có fare do server chốt, nên trả 404 chứ không tạo nghĩa vụ mới.
    /// Method client gửi chỉ áp dụng khi nghĩa vụ còn mở (P1-1); payment đã chốt thì trả hiện trạng.
    /// </summary>
    public async Task<PaymentResponse> CreatePaymentAsync(
        Guid tripId,
        PaymentMethod method,
        Guid actorId,
        bool isPrivileged,
        CancellationToken cancellationToken = default)
    {
        var paymentRequestKey = EventContracts.BuildPaymentRequestKey(tripId);
        var payment = await _processor.FindObligationAsync(tripId, paymentRequestKey, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Chưa có nghĩa vụ thanh toán cho trip {tripId}. Chờ TripService phát RideCompleted (dropoff) rồi gọi lại.");

        EnsureOwner(payment, actorId, isPrivileged);

        if (payment.IsRideOutcomeFinal)
            return PaymentResponse.From(payment);

        // Rider đổi method ngay trong request charge — PATCH /method là đường chuyên biệt,
        // nhưng POST /payments/{tripId} cũng phải tôn trọng lựa chọn (P1-1).
        if (payment.Method != method && payment.Status != PaymentStatus.Charging)
        {
            payment.ChangeMethod(method);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new InvalidPaymentTransitionException(
                    $"Payment {payment.Id} vừa được phiên khác thay đổi, thử lại sau.");
            }
        }

        await _processor.ChargeAsync(payment, Guid.NewGuid(), null, cancellationToken);
        return PaymentResponse.From(await ReloadAsync(payment.Id, cancellationToken));
    }

    /// <summary>Retry thủ công một payment đã Failed: reset hạn mức retry rồi charge lại.</summary>
    public async Task<PaymentResponse> RetryPaymentAsync(
        Guid paymentId,
        Guid actorId,
        bool isPrivileged,
        CancellationToken cancellationToken = default)
    {
        var payment = await LoadAsync(paymentId, cancellationToken);
        EnsureOwner(payment, actorId, isPrivileged);

        return await RetryLoadedAsync(payment, paymentId, cancellationToken);
    }

    /// <summary>
    /// Retry theo trip — rider chưa biết payment id. Tra nghĩa vụ bằng cùng key mà
    /// RideCompleted/TripService dùng, rồi đi đúng đường retry thủ công.
    /// </summary>
    public async Task<PaymentResponse> RetryPaymentByTripAsync(
        Guid tripId,
        Guid actorId,
        bool isPrivileged,
        CancellationToken cancellationToken = default)
    {
        var paymentRequestKey = EventContracts.BuildPaymentRequestKey(tripId);
        var payment = await _processor.FindObligationAsync(tripId, paymentRequestKey, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Chưa có nghĩa vụ thanh toán cho trip {tripId}. Chờ TripService phát RideCompleted (dropoff) rồi gọi lại.");

        EnsureOwner(payment, actorId, isPrivileged);

        return await RetryLoadedAsync(payment, payment.Id, cancellationToken);
    }

    /// <summary>
    /// Phần chung của hai đường retry: reset hạn mức (chỉ payment Failed mới mở lại được, domain
    /// ném InvalidPaymentTransitionException cho trạng thái khác) rồi charge qua processor.
    /// </summary>
    private async Task<PaymentResponse> RetryLoadedAsync(
        PaymentEntity payment,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        payment.PrepareForManualRetry();
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidPaymentTransitionException(
                $"Payment {paymentId} vừa được phiên khác thay đổi, thử lại sau.");
        }

        await _processor.ChargeAsync(payment, Guid.NewGuid(), null, cancellationToken);
        return PaymentResponse.From(await ReloadAsync(payment.Id, cancellationToken));
    }

    /// <summary>
    /// Đổi phương thức khi nghĩa vụ còn mở (P1-1). Charging bị khóa giữa chừng — domain
    /// ChangeMethod ném 409; rider đợi worker reclaim lease rồi mới đổi.
    /// </summary>
    public async Task<PaymentResponse> UpdateMethodAsync(
        Guid paymentId,
        PaymentMethod method,
        Guid actorId,
        bool isPrivileged,
        CancellationToken cancellationToken = default)
    {
        var payment = await LoadAsync(paymentId, cancellationToken);
        EnsureOwner(payment, actorId, isPrivileged);

        payment.ChangeMethod(method);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidPaymentTransitionException(
                $"Payment {paymentId} vừa được phiên khác thay đổi, thử lại sau.");
        }

        return PaymentResponse.From(payment);
    }

    public async Task<PaymentResponse?> GetPaymentAsync(
        Guid id, Guid actorId, bool canViewAll, CancellationToken cancellationToken = default)
    {
        var payment = await _db.Payments.FindAsync(new object[] { id }, cancellationToken);
        if (payment is null)
            return null;

        if (!canViewAll && payment.RiderId != actorId)
            throw new UnauthorizedAccessException("Bạn không có quyền xem payment này.");

        return PaymentResponse.From(payment);
    }

    /// <summary>
    /// Nghĩa vụ theo trip. Rider đang PaymentPending chưa có PaymentId trên trip — endpoint
    /// này trả nguyên trạng nghĩa vụ (kèm id để retry/PATCH method) ngay cả khi RideCompleted
    /// chưa tới (404 → chưa có nghĩa vụ).
    /// </summary>
    public async Task<PaymentResponse?> GetPaymentByTripAsync(
        Guid tripId, Guid actorId, bool canViewAll, CancellationToken cancellationToken = default)
    {
        var key = EventContracts.BuildPaymentRequestKey(tripId);
        var payment = await _processor.FindObligationAsync(tripId, key, cancellationToken);
        if (payment is null)
            return null;

        if (!canViewAll && payment.RiderId != actorId)
            throw new UnauthorizedAccessException("Bạn không có quyền xem payment này.");

        return PaymentResponse.From(payment);
    }

    public async Task<PagedResult<PaymentResponse>> ListPaymentsAsync(
        Guid? riderId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // Chặn ở tầng ứng dụng chứ không dựa vào Math.Clamp của endpoint: caller nội bộ đi thẳng
        // vào service mà truyền page 0 thì Skip(-pageSize) ném InvalidOperationException -> 500.
        if (page < 1)
            throw new ArgumentException("Page phải >= 1.", nameof(page));
        if (pageSize is < 1 or > MaxPageSize)
            throw new ArgumentException($"PageSize phải trong khoảng 1..{MaxPageSize}.", nameof(pageSize));

        var query = riderId is null ? _db.Payments : _db.Payments.Where(p => p.RiderId == riderId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<PaymentResponse>(page, pageSize, totalCount, items.Select(PaymentResponse.From).ToList());
    }

    public async Task<PagedResult<PaymentAuditRow>> QueryForAdminAsync(
        string? status,
        DateTime? from,
        DateTime? to,
        Guid? tripId,
        Guid? riderId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // from/to đến từ query string nên Kind thường là Unspecified; ToUniversalTime chỉ đổi hệ
        // quy chiếu khi đã là Utc. Chuẩn hóa cả hai về cùng một trục để 'from' > 'to' có nghĩa,
        // thay vì so sánh khập khiễng rồi lọt qua validate.
        if (from is not null && to is not null && NormalizeUtc(from.Value) > NormalizeUtc(to.Value))
            throw new ArgumentException("Khoảng thời gian không hợp lệ: 'from' phải <= 'to'.");

        if (page < 1)
            throw new ArgumentException("Page phải >= 1.", nameof(page));
        if (pageSize is < 1 or > MaxPageSize)
            throw new ArgumentException($"PageSize phải trong khoảng 1..{MaxPageSize}.", nameof(pageSize));

        var query = _db.Payments.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            // Enum.TryParse chấp nhận cả số ngoài tập giá trị ('99' -> true), nên chặn thêm bằng IsDefined.
            if (!Enum.TryParse<PaymentStatus>(status, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                throw new ArgumentException($"Trạng thái '{status}' không hợp lệ.");
            }

            query = query.Where(p => p.Status == parsed);
        }

        var fromUtc = from is null ? (DateTime?)null : NormalizeUtc(from.Value);
        var toUtc = to is null ? (DateTime?)null : NormalizeUtc(to.Value);

        if (fromUtc is not null) query = query.Where(p => p.CreatedAtUtc >= fromUtc);
        if (toUtc is not null) query = query.Where(p => p.CreatedAtUtc <= toUtc);
        if (tripId is not null) query = query.Where(p => p.TripId == tripId);
        if (riderId is not null) query = query.Where(p => p.RiderId == riderId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var rows = items.Select(p => new PaymentAuditRow(
            p.Id, p.TripId, p.RiderId, p.Amount, p.Method.ToString(), p.Status.ToString(),
            p.FailureCode, p.CorrelationId, p.CreatedAtUtc)).ToList();

        return new PagedResult<PaymentAuditRow>(page, pageSize, totalCount, rows);
    }

    /// <summary>
    /// Chi tiết một payment cho admin. Không kiểm tra sở hữu vì route đã chốt payments.admin
    /// và role Admin; trả null khi không tồn tại để endpoint phát 404.
    /// </summary>
    public async Task<PaymentResponse?> GetPaymentForAdminAsync(
        Guid paymentId, CancellationToken cancellationToken = default)
    {
        if (paymentId == Guid.Empty)
            throw new ArgumentException("PaymentId không được để trống.", nameof(paymentId));

        var payment = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        return payment is null ? null : PaymentResponse.From(payment);
    }

    private async Task<PaymentEntity> LoadAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await _db.Payments.FindAsync(new object[] { paymentId }, cancellationToken)
        ?? throw new KeyNotFoundException($"Không tìm thấy payment {paymentId}.");

    /// <summary>UTC giữ nguyên; Unspecified được coi là UTC (giá trị từ query string không kèm offset).</summary>
    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// Rider chỉ thao tác được nghĩa vụ của chính mình; admin (quyền payments.refund/create do
    /// role Admin bypass) mới được thao tác hộ. Thiếu check này thì biết TripId là charge được tiền người khác.
    /// </summary>
    private static void EnsureOwner(PaymentEntity payment, Guid actorId, bool isPrivileged)
    {
        if (!isPrivileged && payment.RiderId != actorId)
            throw new UnauthorizedAccessException("Payment này không thuộc tài khoản của bạn.");
    }

    /// <summary>Đọc lại trạng thái cuối từ DB — sau mất race, entity in-memory không còn đáng tin.</summary>
    private async Task<PaymentEntity> ReloadAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId, cancellationToken);
}