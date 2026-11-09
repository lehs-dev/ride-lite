using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TripService.Infrastructure.Persistence;

/// <summary>Readiness (P1-3): TripDB phải kết nối được — đỏ khi SQL Server chưa sẵn sàng.</summary>
public sealed class TripDbHealthCheck(TripDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var ok = await db.Database.CanConnectAsync(cancellationToken);
        return ok
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Không kết nối được SQL Server TripDB.");
    }
}
