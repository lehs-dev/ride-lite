using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SwiftRide.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSwiftRideTelemetry("SwiftRide.ApiGateway");
builder.Services.AddSwiftRideAuthentication(builder.Configuration);
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// CORS cho frontend chạy ở origin khác (vd. http://localhost:3000).
// Danh sách origin lấy từ Cors:AllowedOrigins; để trống = không cho origin nào.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                .WithHeaders("Authorization", "Content-Type", "Idempotency-Key")
                .WithExposedHeaders("X-Correlation-ID")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        }
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("rider-booking", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User.FindFirst("sub")?.Value
                          ?? context.Connection.RemoteIpAddress?.ToString()
                          ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 10,
                Window               = TimeSpan.FromSeconds(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    options.AddPolicy("payment", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User.FindFirst("sub")?.Value
                          ?? context.Connection.RemoteIpAddress?.ToString()
                          ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 5,
                Window               = TimeSpan.FromSeconds(60),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // Rate Limiter chống brute-force mật khẩu cổng Admin (5 req / 60s)
    options.AddPolicy("admin-login-rate", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 5,
                Window               = TimeSpan.FromSeconds(60),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // Rate Limiter riêng cho Admin Dashboard (100 req / 10s)
    options.AddPolicy("admin-dashboard-rate", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User.FindFirst("sub")?.Value
                          ?? context.Connection.RemoteIpAddress?.ToString()
                          ?? "admin-anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 100,
                Window               = TimeSpan.FromSeconds(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 10,
            }));

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 30,
                Window               = TimeSpan.FromSeconds(10),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 2,
            }));
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseSwiftRideCorrelationId();
// CORS trước auth/rate limit để preflight OPTIONS không bị 401 hoặc bị tính vào rate limit.
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }))
    .AllowAnonymous();

app.MapReverseProxy();
app.Run();
