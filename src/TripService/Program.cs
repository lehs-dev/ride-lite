using Microsoft.EntityFrameworkCore;
using SwiftRide.Security;
using TripService.Api;
using TripService.Application.Services;
using TripService.Domain.Repositories;
using TripService.Infrastructure.Persistence;
using TripService.Infrastructure.Repositories;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using TripService.Application.DTOs;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSwiftRideTelemetry("SwiftRide.TripService");
builder.Services.AddSwiftRideAuthentication(builder.Configuration);

// CSDL SQL Server (TripDB) cho TripService và Identity/RBAC
var tripDbConnection = builder.Configuration.GetConnectionString("TripDb")
    ?? "Server=127.0.0.1,1433;Database=TripDB;User Id=sa;Password=SwiftRideDev@2026!;Encrypt=True;TrustServerCertificate=True";

builder.Services.AddDbContext<TripDbContext>(options =>
    options.UseSqlServer(tripDbConnection));

builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITripRepository, TripRepository>();
builder.Services.AddScoped<ITripService,  TripApplicationService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Dán accessToken nhận được sau khi đăng nhập."
    });

    options.AddSecurityRequirement(document =>
        new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("bearer", document)] = []
        });
});
var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

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
app.MapPost("/trips", async (
    CreateTripRequest request,
    ClaimsPrincipal user,
    ITripService tripService,
    CancellationToken cancellationToken) =>
{
    var userIdText = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user.FindFirst("sub")?.Value;

    if (!Guid.TryParse(userIdText, out var riderId)
        || riderId == Guid.Empty)
    {
        return Results.Unauthorized();
    }

    try
    {
        var tripId = await tripService.CreateAsync(
            riderId,
            request,
            cancellationToken);

        return Results.Created(
            $"/trips/{tripId}",
            new { id = tripId });
    }
    catch (ValidationException ex)
    {
        return Results.Problem(
            title: "Dữ liệu đặt chuyến không hợp lệ",
            detail: ex.Message,
            statusCode: StatusCodes.Status400BadRequest);
    }
})
.RequirePermission(PermissionsCatalog.TripsCreate);

app.MapGet("/trips/{id:guid}", async (
    Guid id,
    ClaimsPrincipal user,
    ITripService tripService,
    CancellationToken cancellationToken) =>
{
    var userIdText = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user.FindFirst("sub")?.Value;

    if (!Guid.TryParse(userIdText, out var riderId)
        || riderId == Guid.Empty)
    {
        return Results.Unauthorized();
    }

    var trip = await tripService.GetByIdAsync(
        id,
        riderId,
        cancellationToken);

    if (trip is null)
    {
        return Results.NotFound(new
        {
            message = "Không tìm thấy chuyến đi."
        });
    }

    return Results.Ok(trip);
})
.RequirePermission(PermissionsCatalog.TripsRead);

app.MapPost("/trips/{id:guid}/driver-accept", (Guid id) => Results.Problem(
    title: "Not implemented",
    detail: "Trip application handler has not been implemented.",
    statusCode: StatusCodes.Status501NotImplemented))
    .RequirePermission(PermissionsCatalog.TripsAccept);

app.Run();
