using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PaymentService.Infrastructure.Persistence;

/// <summary>Readiness (P1-3): PayDB phải kết nối được — đỏ khi PostgreSQL chưa sẵn sàng.</summary>
public sealed class PaymentDbHealthCheck(PaymentDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var ok = await PaymentDbInitializer.CanConnectAsync(db, cancellationToken);
        return ok
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Không kết nối được PostgreSQL PayDB.");
    }
}
