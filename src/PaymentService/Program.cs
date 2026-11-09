using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentService.Api;
using PaymentService.Application.Abstractions;
using PaymentService.Application.Services;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.Payment;
using PaymentService.Infrastructure.Persistence;
using PaymentService.Infrastructure.Services;
using SwiftRide.Security;
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSwiftRideTelemetry("SwiftRide.PaymentService");
builder.Services.AddSwiftRideAuthentication(builder.Configuration);

// PayDB (PostgreSQL) — env ConnectionStrings__PaymentDb từ compose.yaml thắng cấu hình file.
var paymentDbConnection = builder.Configuration.GetConnectionString("PaymentDb")
    ?? "Host=localhost;Port=5432;Database=PayDB;Username=swiftdev";

builder.Services.AddDbContext<PaymentDbContext>(options => options.UseNpgsql(paymentDbConnection));

builder.Services.AddOptions<MoMoSandboxOptions>()
    .BindConfiguration(MoMoSandboxOptions.SectionName);
builder.Services.AddOptions<RabbitMqOptions>()
    .BindConfiguration(RabbitMqOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.Host), "RabbitMq:Host là bắt buộc.");

builder.Services.AddSingleton<IPaymentGateway, MoMoSandboxGateway>();
builder.Services.AddScoped<IPaymentEventPublisher, PaymentEventPublisher>();
builder.Services.AddScoped<PaymentProcessor>();
builder.Services.AddScoped<IPaymentAppService, PaymentAppService>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<RideCompletedConsumer, RideCompletedConsumerDefinition>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbit = context.GetRequiredService<Microsoft.Extensions.Options.IOptions<RabbitMqOptions>>().Value;
        cfg.Host(rabbit.Host, (ushort)rabbit.Port, rabbit.VirtualHost, h =>
        {
            h.Username(rabbit.UserName);
            h.Password(rabbit.Password);
        });
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHostedService<OutboxDispatcherWorker>();
builder.Services.AddHostedService<PaymentBackgroundWorker>();

// Readiness kiểm tra dependency thật (P1-3): DB phải kết nối được, broker phải start được.
// Live không đụng gì cả hai — process sống là live xanh; ready đỏ khi dependency lỗi.
// FailureStatus mặc định là Unhealthy → /health/ready trả 503 (xem SwiftRideHealthReportWriter).
builder.Services.AddHealthChecks()
    .AddCheck<PaymentDbHealthCheck>("paydb")
    .AddCheck<SwiftRide.Security.RabbitMqHealthCheck>("rabbitmq");

var app = builder.Build();

app.UseSwiftRideProblemDetails();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    var startup = await PaymentDbInitializer.InitializeWithRetryAsync(db, app.Logger);
    if (startup.ShouldAbortStartup)
    {
        // Fail-fast (P1-3): service chưa hội tụ được schema thì mọi query Payments/ProcessedEvents
        // sẽ 500 — mở cổng chỉ tạo "green process, red behavior". Abort để orchestrator restart
        // và /health/live phản ánh đúng sự thật. InitializeWithRetryAsync đã retry hữu hạn cho
        // trường hợp Postgres chưa dậy (~60s), nên đây là DB sai thật hoặc vẫn chưa tới.
        app.Logger.LogCritical(
            "PaymentService dừng khởi động: PayDB chưa hội tụ schema sau {Max} lần thử (NeverConnected={NeverConnected}).",
            PaymentDbInitializer.MaxUnreachableAttempts, startup.NeverConnected);
        throw new InvalidOperationException(
            $"Không hội tụ được schema PayDB (NeverConnected={startup.NeverConnected}) — abort startup.");
    }
}

app.UseHttpsRedirection();
app.UseSwiftRideCorrelationId(); // Trước auth — OnStarting bắt cả 401/403/429
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { service = "PaymentService", status = "Healthy" }))
    .AllowAnonymous();
// Body liệt kê từng check + mã HTTP tường minh: dependency lỗi là 503, không bao giờ 200 "Degraded".
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = SwiftRideHealthReportWriter.WriteAsync,
}).AllowAnonymous();

app.MapPaymentEndpoints();
app.MapAdminPaymentEndpoints();

app.Run();
