using GatewayChargeResultDto = PaymentService.Application.Abstractions.GatewayChargeResult;

namespace PaymentService.Domain.Entities;

/// <summary>
/// Một nghĩa vụ thanh toán cho một trip. Idempotency do hai unique index đảm bảo:
/// TripId và PaymentRequestKey — duplicate RideCompleted không bao giờ tạo row thứ hai.
/// Status là concurrency token: hai bên claim cùng lúc chỉ đúng một bên ghi được, bên kia
/// nhận DbUpdateConcurrencyException và vì thế không bao giờ gọi gateway.
/// </summary>
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public string PaymentRequestKey { get; set; } = string.Empty;
    public Guid RiderId { get; set; }
    public Guid? DriverId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "VND";
    public PaymentMethod Method { get; set; } = PaymentMethod.MoMoApp;
    public PaymentStatus Status { get; set; } = PaymentStatus.Created;

    public string? GatewayPartnerCode { get; set; }
    public string? GatewayOrderRef { get; set; }
    public string? GatewayTransId { get; set; }
    public string? SignatureChecksum { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }

    /// <summary>Số lần đã gọi gateway; chỉ tăng khi kết quả retryable, có trần MaxGatewayAttempts.</summary>
    public int AttemptCount { get; set; }

    public Guid? SettledEventId { get; set; }
    public string? CorrelationId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SettledAtUtc { get; set; }
    public DateTime? FailedAtUtc { get; set; }

    public static Payment CreateFromRide(
        Guid tripId,
        string paymentRequestKey,
        Guid riderId,
        Guid? driverId,
        decimal amount,
        string currency,
        PaymentMethod method,
        string? correlationId)
    {
        // Ràng buộc độ dài/dạng ở đây phản chiếu đúng maxLength trong PayDB (varchar(128) cho
        // key, varchar(3) cho currency): vi phạm phải thành ArgumentException ngay — message rơi
        // _error rõ ràng — thay vì DbUpdateException mù do lỗi cột. Trim trước khi đo: khoảng
        // trắng đầu/cuối không được tính vào giới hạn của cột.
        if (string.IsNullOrWhiteSpace(paymentRequestKey))
            throw new ArgumentException("PaymentRequestKey is required.", nameof(paymentRequestKey));

        // Đo độ dài trên chuỗi GỐC trước khi trim: nếu chỉ đo sau trim thì một key 130 ký tự
        // (2 khoảng trắng + 128 ký tự thật) lọt qua domain rồi mới chết ở varchar(128) của DB,
        // trả về DbUpdateException mù thay vì message nghiệp vụ rõ ràng. Key lưu xuống DB là bản
        // đã trim để không phình index unique và không lệch với quy ước trip:{id:N}.
        if (paymentRequestKey.Length > PaymentPolicy.MaxPaymentRequestKeyLength)
            throw new ArgumentException(
                $"PaymentRequestKey dài quá {PaymentPolicy.MaxPaymentRequestKeyLength} ký tự.",
                nameof(paymentRequestKey));

        var normalizedKey = paymentRequestKey.Trim();
        if (amount <= 0)
            throw new ArgumentException($"Amount phải > 0, nhận được {amount}.", nameof(amount));

        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency là bắt buộc.", nameof(currency));

        var normalizedCurrency = currency.Trim();
        if (normalizedCurrency.Length != PaymentPolicy.CurrencyCodeLength)
            throw new ArgumentException(
                $"Currency phải đúng {PaymentPolicy.CurrencyCodeLength} ký tự, nhận được '{currency}'.",
                nameof(currency));
        if (!normalizedCurrency.All(char.IsLetter))
            throw new ArgumentException(
                $"Currency phải chỉ gồm chữ cái ISO-4217, nhận được '{currency}'.",
                nameof(currency));

        if (!PaymentPolicy.IsSupportedMethod(method))
            throw new ArgumentException(
                $"Phương thức thanh toán không được hỗ trợ: {(short)method}.", nameof(method));

        var now = DateTime.UtcNow;
        return new Payment
        {
            TripId = tripId,
            PaymentRequestKey = normalizedKey,
            RiderId = riderId,
            DriverId = driverId,
            Amount = amount,
            Currency = normalizedCurrency.ToUpperInvariant(),
            Method = method,
            Status = PaymentStatus.Created,
            CorrelationId = correlationId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    public bool IsSettled => Status == PaymentStatus.Settled;

    public bool IsTerminalFailure => Status == PaymentStatus.Failed;

    /// <summary>
    /// Trạng thái chốt về tiền. Charging KHÔNG thuộc tập này: đó là phiên đang chạy,
    /// redelivery vẫn phải được xét tiếp — và bị chặn bằng claim, không bằng IsClosed.
    /// </summary>
    public bool IsClosed => Status is PaymentStatus.Settled or PaymentStatus.Failed;

    /// <summary>Nghĩa vụ RideCompleted đã có kết quả cuối → inbox claim được an toàn.</summary>
    public bool IsRideOutcomeFinal => Status is PaymentStatus.Settled or PaymentStatus.Failed;

    public bool IsChargingLeaseExpired(DateTime nowUtc) =>
        Status == PaymentStatus.Charging && UpdatedAtUtc + PaymentPolicy.ChargingLease <= nowUtc;

    /// <summary>Ghi nhận tham chiếu trả về từ sandbox sau mỗi lần gọi charge.</summary>
    public void RecordGatewayAttempt(GatewayChargeResultDto result)
    {
        GatewayPartnerCode = result.PartnerCode;
        GatewayOrderRef = result.OrderRef;
        GatewayTransId = result.TransId;
        SignatureChecksum = result.Checksum;
        Touch();
    }

    /// <summary>
    /// Claim nghĩa vụ cho một phiên charge. Status thay đổi thật (Created/PendingRetry → Charging)
    /// nên EF luôn phát sinh UPDATE kèm "WHERE Status = @orig" — hai bên claim song song chỉ một bên thắng.
    /// </summary>
    public void MarkCharging()
    {
        EnsureMutable();

        if (Status == PaymentStatus.Charging)
            throw new InvalidPaymentTransitionException(
                $"Payment {Id} đang có phiên charge khác (Charging từ {UpdatedAtUtc:O}).");

        Status = PaymentStatus.Charging;
        Touch();
    }

    /// <summary>
    /// Lease của một phiên Charging quá hạn (process chết giữa gateway call và lần ghi kết quả):
    /// đưa về PendingRetry để worker reclaim. Có đếm AttemptCount để crash loop không retry vô hạn.
    /// </summary>
    public void MarkChargingLeaseExpired()
    {
        if (Status != PaymentStatus.Charging)
            throw new InvalidPaymentTransitionException(
                $"Chỉ reclaim được lease của payment Charging, hiện đang ở {Status}.");

        Status = PaymentStatus.PendingRetry;
        FailureCode = PaymentPolicy.ChargingLeaseExpiredCode;
        FailureMessage = "Phiên charge trước không chốt được kết quả (crash/restart) — reclaim để thử lại.";
        AttemptCount++;
        Touch();
    }

    /// <summary>
    /// Nghĩa vụ thu tiền mặt: không có gateway call, nên phiên charge chốt Authorized ngay
    /// và để SettleAsync phát PaymentSettled. Vẫn đi qua cùng một trạng thái trung gian để
    /// bất biến "chỉ settle từ Authorized" (MarkSettled) giữ nguyên cho mọi method.
    /// </summary>
    public void MarkCashCollected()
    {
        EnsureMutable();

        if (Status == PaymentStatus.Charging)
            throw new InvalidPaymentTransitionException(
                $"Payment {Id} đang có phiên charge điện tử chạy, không chốt tiền mặt giữa chừng.");

        if (!PaymentPolicy.IsOfflineCollection(Method))
            throw new InvalidPaymentTransitionException(
                $"Chỉ chốt trực tiếp được payment Cash, hiện method là {Method}.");

        Status = PaymentStatus.Authorized;
        FailureCode = null;
        FailureMessage = null;
        Touch();
    }

    /// <summary>Trạng thái nghiệp vụ do Application/Consumer quyết định (có EnsureMutable).</summary>
    public void MarkAuthorized()
    {
        EnsureMutable();
        Status = PaymentStatus.Authorized;
        FailureCode = null;
        FailureMessage = null;
        Touch();
    }

    public void MarkSettled(Guid settledEventId)
    {
        if (Status == PaymentStatus.Settled)
        {
            if (SettledEventId != settledEventId)
                throw new InvalidPaymentTransitionException(
                    $"Payment {Id} đã settled với event {SettledEventId}, không chấp nhận event {settledEventId}.");
            return;
        }

        if (Status != PaymentStatus.Authorized)
            throw new InvalidPaymentTransitionException(
                $"Chỉ có thể settle payment ở trạng thái Authorized, hiện đang ở {Status}.");

        Status = PaymentStatus.Settled;
        SettledEventId = settledEventId;
        SettledAtUtc = DateTime.UtcNow;
        FailureCode = null;
        FailureMessage = null;
        Touch();
    }

    public void MarkPendingRetry(string code, string message)
    {
        if (Status is PaymentStatus.Settled or PaymentStatus.Failed)
        {
            throw new InvalidPaymentTransitionException($"Payment {Id} đã ở trạng thái chốt ({Status}).");
        }

        Status = PaymentStatus.PendingRetry;
        FailureCode = code;
        FailureMessage = message;
        AttemptCount++;
        Touch();
    }

    public void MarkFailed(string code, string message)
    {
        if (Status is PaymentStatus.Settled or PaymentStatus.Failed)
        {
            throw new InvalidPaymentTransitionException(
                $"Không hạ cấp payment {Id} đang ở {Status} xuống Failed.");
        }

        Status = PaymentStatus.Failed;
        FailureCode = code;
        FailureMessage = message;
        FailedAtUtc = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Rider/admin đổi phương thức thanh toán khi nghĩa vụ còn mở. Charging bị khóa vì một
    /// phiên gateway đang chạy với intent đã build theo method cũ; lease hết hạn thì worker
    /// đưa về PendingRetry/Charging reclaim — lúc đó mới đổi được.
    /// </summary>
    public void ChangeMethod(PaymentMethod method)
    {
        EnsureMutable();

        if (Status == PaymentStatus.Charging)
            throw new InvalidPaymentTransitionException(
                $"Payment {Id} đang có phiên charge chạy, không đổi phương thức giữa chừng.");

        // Method là một phần của hợp đồng idempotency (VerifyOrThrow so payload event với
        // nghĩa vụ đã chốt): nhận giá trị ngoài enum sẽ tạo row không re-projection được.
        if (!PaymentPolicy.IsSupportedMethod(method))
            throw new ArgumentException(
                $"Phương thức thanh toán không được hỗ trợ: {(short)method}.", nameof(method));

        Method = method;
        Touch();
    }

    /// <summary>
    /// Rider/admin chủ động thử lại một payment đã Failed: mở lại nghĩa vụ, xóa dấu vết lỗi
    /// và reset AttemptCount để có tối đa MaxGatewayAttempts lượt mới.
    /// </summary>
    public void PrepareForManualRetry()
    {
        if (Status != PaymentStatus.Failed)
            throw new InvalidPaymentTransitionException(
                $"Chỉ retry được payment Failed, hiện đang ở {Status}.");

        Status = PaymentStatus.Created;
        FailureCode = null;
        FailureMessage = null;
        AttemptCount = 0;
        FailedAtUtc = null;
        Touch();
    }

    private void EnsureMutable()
    {
        if (Status is PaymentStatus.Settled or PaymentStatus.Failed)
        {
            throw new InvalidPaymentTransitionException($"Payment {Id} đã ở trạng thái chốt ({Status}).");
        }
    }

    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;
}
