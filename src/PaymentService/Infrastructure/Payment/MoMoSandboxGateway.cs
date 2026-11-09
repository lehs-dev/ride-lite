using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PaymentService.Application.Abstractions;

namespace PaymentService.Infrastructure.Payment;

/// <summary>
/// MoMo sandbox giả lập: gọi đồng bộ + latency nhân tạo, kết quả tất định theo số tiền
/// để E2E/test assert được cả nhánh thành công lẫn thất bại mà không cần random.
/// </summary>
public sealed class MoMoSandboxGateway : IPaymentGateway
{
    public const string CodeSuccess = "SUCCESS";
    public const string CodeInvalidAmount = "INVALID_AMOUNT";
    public const string CodeAmountLimit = "AMOUNT_LIMIT_EXCEEDED";
    public const string CodeInsufficientFunds = "INSUFFICIENT_FUNDS";
    public const string CodeGatewayUnavailable = "GATEWAY_UNAVAILABLE";
    public const string CodeSignatureInvalid = "SIGNATURE_INVALID";

    /// <summary>
    /// Magic trigger theo CHỮ SỐ TẬN CÙNG (VND không lẻ): amount kết thúc bằng 3 → thiếu
    /// số dư; kết thúc bằng 7 → gateway lỗi tạm thời. Chọn một chữ số vì fare làm tròn 500
    /// nên hai chữ số cuối chỉ có thể là 00 hoặc 00/500-pattern.
    /// </summary>
    private const decimal InsufficientFundsLastDigit = 3m;
    private const decimal UnavailableLastDigit = 7m;

    private readonly MoMoSandboxOptions _options;
    private readonly ILogger<MoMoSandboxGateway> _logger;

    public MoMoSandboxGateway(IOptions<MoMoSandboxOptions> options, ILogger<MoMoSandboxGateway> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GatewayChargeResult> ChargeAsync(PaymentIntent intent, CancellationToken cancellationToken = default)
    {
        if (intent.Amount <= 0 || !string.Equals(intent.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            return Fail(CodeInvalidAmount, "Số tiền hoặc tiền tệ không hợp lệ.");

        if (intent.Amount > _options.MaxAmountVnd)
            return Fail(CodeAmountLimit, $"Vượt hạn mức sandbox {_options.MaxAmountVnd} VND.");

        await SimulateLatency(cancellationToken);

        if (intent.Amount % 10 == InsufficientFundsLastDigit)
            return Fail(CodeInsufficientFunds, "Số dư ví MoMo không đủ (sandbox).");

        if (intent.Amount % 10 == UnavailableLastDigit)
            throw new HttpRequestException("MoMo sandbox tạm thời không phản hồi.");

        var orderRef = BuildOrderRef(intent.TripId);
        var checksum = ComputeChecksum(orderRef, intent.Amount, intent.Currency);

        _logger.LogInformation(
            "MoMo sandbox charge OK. PaymentId={PaymentId} TripId={TripId} OrderRef={OrderRef} Amount={Amount}",
            intent.PaymentId, intent.TripId, orderRef, intent.Amount);

        return new GatewayChargeResult(
            Success: true,
            Retryable: false,
            Code: CodeSuccess,
            Message: "Giao dịch sandbox thành công.",
            PartnerCode: _options.PartnerCode,
            OrderRef: orderRef,
            TransId: Guid.NewGuid().ToString("N"),
            Checksum: checksum);
    }

    private static string BuildOrderRef(Guid tripId) => $"SR{tripId:N}"[..24];

    internal string ComputeChecksum(string orderRef, decimal amount, string currency)
    {
        var data = $"{_options.PartnerCode}|{orderRef}|{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{currency}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.SignatureSecret), Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task SimulateLatency(CancellationToken cancellationToken)
    {
        if (_options.SimulatedLatencyMs > 0)
            await Task.Delay(_options.SimulatedLatencyMs, cancellationToken);
    }

    private static GatewayChargeResult Fail(string code, string message) =>
        new(false, Retryable: false, code, message);

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}