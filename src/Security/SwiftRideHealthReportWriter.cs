using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace SwiftRide.Security;

/// <summary>
/// Readiness (P1-3): body /health/ready phải nói được dependency nào đỏ, không chỉ trả "Degraded".
/// Writer dùng chung cho mọi service để payload nhất quán và testable.
/// </summary>
/// <remarks>
/// Mapping HTTP theo đúng mặc định của ASP.NET Core để không đổi hành vi orchestrator:
/// Healthy/Degraded → 200, Unhealthy → 503. Vì thế một check dependency phải trả
/// <c>Unhealthy</c> (không phải Degraded) khi service thật sự chưa phục vụ được.
/// </remarks>
public static class SwiftRideHealthReportWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Mã HTTP mà health-report pipeline sẽ trả cho trạng thái này.</summary>
    public static int GetStatusCode(HealthStatus status) =>
        status == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        var payload = new HealthPayload(
            report.Status.ToString(),
            GetStatusCode(report.Status),
            report.TotalDuration.TotalMilliseconds,
            context.RequestServices
                .GetService<IHostApplicationLifetime>() is { } lifetime
                && !lifetime.ApplicationStarted.IsCancellationRequested
                ? "starting"
                : "started",
            report.Entries.Select(entry => new CheckPayload(
                    entry.Key,
                    entry.Value.Status.ToString(),
                    entry.Value.Description,
                    entry.Value.Duration.TotalMilliseconds))
                .ToArray());

        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(context.Response.Body, payload, Options, context.RequestAborted);
    }

    private sealed record HealthPayload(
        string Status,
        int StatusCode,
        double TotalDurationMs,
        string Phase,
        IReadOnlyList<CheckPayload> Checks);

    private sealed record CheckPayload(string Name, string Status, string? Description, double DurationMs);
}
