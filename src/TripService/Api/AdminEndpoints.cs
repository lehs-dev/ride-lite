using System.Security.Claims;
using SwiftRide.Security;
using TripService.Application.DTOs;
using TripService.Application.Services;

namespace TripService.Api;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin");

        // 1. Cổng đăng nhập dành riêng cho Quản trị viên
        group.MapPost("/auth/login", async (LoginRequest request, IAuthService authService) =>
        {
            try
            {
                var response = await authService.AdminLoginAsync(request);
                return Results.Ok(response);
            }
            catch (AccessViolationException ex)
            {
                // User có credentials đúng nhưng không có role Admin
                return Results.Problem(
                    title: "Từ chối truy cập",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(
                    title: "Xác thực thất bại",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status401Unauthorized);
            }
        }).AllowAnonymous();

        // 2. Tra cứu danh sách người dùng toàn hệ thống
        group.MapGet("/users", async (string? role, string? keyword, IAuthService authService) =>
        {
            var users = await authService.GetUsersAsync(role, keyword);
            return Results.Ok(users);
        }).RequirePermission(PermissionsCatalog.UsersManage);

        // 3. Khóa hoặc mở khóa tài khoản người dùng
        group.MapPatch("/users/{id:guid}/status", async (
            Guid id,
            UpdateUserStatusRequest request,
            ClaimsPrincipal user,
            IAuthService authService) =>
        {
            var currentAdminIdStr = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value;

            if (!Guid.TryParse(currentAdminIdStr, out var currentAdminId))
                return Results.Unauthorized();

            try
            {
                var updatedUser = await authService.UpdateUserStatusAsync(id, request.IsActive, currentAdminId);
                return Results.Ok(updatedUser);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Hành động không hợp lệ",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        }).RequirePermission(PermissionsCatalog.UsersManage);

        // 4. Giám sát toàn bộ chuyến đi cho Admin
        group.MapGet("/trips", () =>
        {
            // Trả về danh sách chuyến đi mẫu cho Dashboard quản trị
            var mockTrips = new List<AdminTripDto>
            {
                new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "Completed",
                    120000m,
                    DateTime.UtcNow.AddHours(-2)),
                new(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Guid.NewGuid(),
                    null,
                    "Requested",
                    85000m,
                    DateTime.UtcNow.AddMinutes(-10))
            };

            return Results.Ok(mockTrips);
        }).RequirePermission(PermissionsCatalog.TripsRead);

        return app;
    }
}
