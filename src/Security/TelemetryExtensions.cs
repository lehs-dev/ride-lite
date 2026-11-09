using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SwiftRide.Security;

public static class TelemetryExtensions
{
    /// <summary>
    /// Đăng ký OpenTelemetry traces + metrics, export qua OTLP.
    /// Endpoint đọc từ env var OTEL_EXPORTER_OTLP_ENDPOINT (đã set trong compose.yaml).
    /// Khi chạy local ngoài devcontainer, fallback về http://localhost:4317.
    /// </summary>
    public static IServiceCollection AddSwiftRideTelemetry(
        this IServiceCollection services,
        string serviceName)
    {
        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName);

        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetResourceBuilder(resource)
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.RecordException = true;
                    // Bỏ qua health check khỏi traces để không nhiễu
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                })
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(resource)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());

        return services;
    }

    /// <summary>
    /// Gắn X-Correlation-ID (= W3C TraceId) vào mọi response.
    /// Dùng OnStarting để đảm bảo header được gắn kể cả khi response
    /// là 401 / 403 / 429 — những response flush sớm trước khi
    /// control quay lại middleware này.
    /// Đặt TRƯỚC UseAuthentication trong pipeline.
    /// </summary>
    public static IApplicationBuilder UseSwiftRideCorrelationId(
        this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                // Activity.Current đã được OTel AspNetCore instrumentation
                // tạo ra ngay khi request bắt đầu — luôn có giá trị ở đây
                var traceId = Activity.Current?.TraceId.ToString();
                if (!string.IsNullOrEmpty(traceId))
                    ctx.Response.Headers["X-Correlation-ID"] = traceId;

                return Task.CompletedTask;
            });

            await next();
        });
    }

    /// <summary>
    /// Global exception handler thống nhất (P1-3): mọi exception chưa được endpoint tự map
    /// sẽ trả ProblemDetails (RFC 9457) kèm TraceId để đối chiếu log/OTel, thay vì HTML trắng
    /// hoặc thông báo chung của Kestrel. Đặt TRƯỚC UseSwiftRideCorrelationId để OnStarting
    /// của correlation header đăng ký sau cùng và vẫn gắn được lên response lỗi.
    /// </summary>
    public static IApplicationBuilder UseSwiftRideProblemDetails(this IApplicationBuilder app)
    {
        return app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                var logger = context.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    .CreateLogger("SwiftRide.ProblemDetails");

                logger.LogError(feature?.Error, "Unhandled exception on {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/problem+json";

                var traceId = Activity.Current?.TraceId.ToString() ?? "n/a";

                await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Lỗi máy chủ",
                    Detail = $"Yêu cầu không hoàn tất do lỗi hệ thống. Mã theo dõi: {traceId}.",
                    Type = "https://httpstatuses.com/500",
                    Status = StatusCodes.Status500InternalServerError,
                    Instance = context.Request.Path
                });
            });
        });
    }
}
