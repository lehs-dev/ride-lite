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

        // 4. Giám sát toàn bộ chuyến đi cho Admin — query thật từ TripDB
        group.MapGet("/trips", async (
            string? status,
            DateTime? from,
            DateTime? to,
            int? page,
            int? pageSize,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var current = Math.Max(page ?? 1, 1);

            try
            {
                var trips = await service.ListTripsForAdminAsync(status, from, to, current, size, cancellationToken);
                return Results.Ok(trips);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: "Dữ liệu không hợp lệ", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequirePermission(PermissionsCatalog.TripsRead);

        return app;
    }
}
