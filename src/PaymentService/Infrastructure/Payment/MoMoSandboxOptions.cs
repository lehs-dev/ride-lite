namespace PaymentService.Infrastructure.Payment;

/// <summary>Cấu hình sandbox MoMo giả lập (section "MoMoSandbox").</summary>
public class MoMoSandboxOptions
{
    public const string SectionName = "MoMoSandbox";

    public string PartnerCode { get; set; } = "SWIFTRIDE_SANDBOX";

    /// <summary>Chữ ký mock — KHÔNG phải secret thật, chỉ mô phỏng secureParams của MoMo.</summary>
    public string SignatureSecret { get; set; } = "sandbox-development-secret";

    public decimal MaxAmountVnd { get; set; } = 5_000_000m;

    public int SimulatedLatencyMs { get; set; } = 200;
}