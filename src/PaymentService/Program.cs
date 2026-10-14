using SwiftRide.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSwiftRideTelemetry("SwiftRide.PaymentService");
builder.Services.AddSwiftRideAuthentication(builder.Configuration);

var app = builder.Build();

app.UseHttpsRedirection();
app.UseSwiftRideCorrelationId(); // Trước auth — OnStarting bắt cả 401/403/429
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { service = "PaymentService", status = "Healthy" }))
    .AllowAnonymous();

// Áp dụng kiểm tra quyền động (Dynamic RBAC)
app.MapPost("/payments", () => Results.Problem(
    title: "Not implemented",
    detail: "Payment handler has not been implemented.",
    statusCode: StatusCodes.Status501NotImplemented))
    .RequirePermission(PermissionsCatalog.PaymentsCreate);

app.MapPost("/payments/{id:guid}/refund", (Guid id) => Results.Problem(
    title: "Not implemented",
    detail: "Refund handler has not been implemented.",
    statusCode: StatusCodes.Status501NotImplemented))
    .RequirePermission(PermissionsCatalog.PaymentsRefund);

// Endpoint Quản trị kiểm toán thanh toán cho Admin
app.MapGet("/admin/payments", () =>
{
    var mockTransactions = new[]
    {
        new
        {
            TransactionId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            TripId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RiderId = Guid.NewGuid(),
            Amount = 120000m,
            Method = "Wallet",
            Status = "Settled",
            Timestamp = DateTime.UtcNow.AddHours(-1)
        }
    };
    return Results.Ok(mockTransactions);
}).RequirePermission(PermissionsCatalog.PaymentsAdmin);

app.Run();
