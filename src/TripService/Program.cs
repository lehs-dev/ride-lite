using Microsoft.EntityFrameworkCore;
using SwiftRide.Security;
using TripService.Api;
using TripService.Application.Services;
using TripService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSwiftRideTelemetry("SwiftRide.TripService");
builder.Services.AddSwiftRideAuthentication(builder.Configuration);

// CSDL SQL Server (TripDB) cho TripService và Identity/RBAC
var tripDbConnection = builder.Configuration.GetConnectionString("TripDb")
    ?? "Server=127.0.0.1,1433;Database=TripDB;User Id=sa;Password=SwiftRideDev@2026!;Encrypt=True;TrustServerCertificate=True";

builder.Services.AddDbContext<TripDbContext>(options =>
    options.UseSqlServer(tripDbConnection));

builder.Services.AddScoped<IAuthService, AuthService>();

var app = builder.Build();

// Khởi tạo schema và nạp dữ liệu phân quyền mẫu khi DB sẵn sàng
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();
    try
    {
        await TripDbSeeder.SeedAsync(db);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Chưa thể kết nối tới SQL Server TripDB lúc khởi động. Dữ liệu sẽ được nạp khi CSDL sẵn sàng.");
    }
}

app.UseHttpsRedirection();
app.UseSwiftRideCorrelationId(); // Trước auth — OnStarting bắt cả 401/403/429
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { service = "TripService", status = "Healthy" }))
    .AllowAnonymous();

// Ánh xạ bộ API Xác thực & Phân quyền động (RBAC)
app.MapAuthEndpoints();

// Ánh xạ bộ API Phân luồng Quản trị viên (Admin Flow)
app.MapAdminEndpoints();

// Nghiệp vụ chuyến đi với phân quyền động (Dynamic RBAC)
app.MapPost("/trips", () => Results.Problem(
    title: "Not implemented",
    detail: "Trip application handler has not been implemented.",
    statusCode: StatusCodes.Status501NotImplemented))
    .RequirePermission(PermissionsCatalog.TripsCreate);

app.MapPost("/trips/{id:guid}/driver-accept", (Guid id) => Results.Problem(
    title: "Not implemented",
    detail: "Trip application handler has not been implemented.",
    statusCode: StatusCodes.Status501NotImplemented))
    .RequirePermission(PermissionsCatalog.TripsAccept);

app.Run();
