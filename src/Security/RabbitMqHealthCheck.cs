using MassTransit;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SwiftRide.Security;

/// <summary>
/// Readiness (P1-3): bus do AddMassTransit đăng ký singleton và host tự StartAsync.
/// CheckHealth trả về tình trạng broker + toàn bộ receive endpoint tại thời điểm gọi —
/// broker down hoặc endpoint chưa sẵn sàng là đỏ.
/// </summary>
public sealed class RabbitMqHealthCheck(IBusControl bus) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var health = bus.CheckHealth();
        // Mapping HTTP: writer mặc định của ASP.NET Core chỉ trả 503 cho Unhealthy (Degraded vẫn 200),
        // nên mọi trạng thái không healthy đều phải là Unhealthy để /health/ready thật sự đỏ.
        return Task.FromResult(health.IsHealthy()
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"RabbitMQ chưa sẵn sàng: {health}"));
    }
}

internal static class BusHealthResultExtensions
{
    /// <summary>BusHealthResult không expose enum công khai — suy ra qua ToString khi build.</summary>
    internal static bool IsHealthy(this MassTransit.BusHealthResult? health) =>
        health?.ToString()?.Contains("Healthy", StringComparison.OrdinalIgnoreCase) == true;
}
