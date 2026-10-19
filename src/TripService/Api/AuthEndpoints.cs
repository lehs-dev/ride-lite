using System.Security.Claims;
using SwiftRide.Security;
using TripService.Application.DTOs;
using TripService.Application.Services;

namespace TripService.Api;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth");

        group.MapPost("/login", async (LoginRequest request, IAuthService authService) =>
        {
            try
            {
                var response = await authService.LoginAsync(request);
                return Results.Ok(response);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(
                    title: "Xác thực thất bại",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status401Unauthorized);
            }
        }).AllowAnonymous();

        group.MapPost("/register", async (RegisterRequest request, IAuthService authService) =>
        {
            try
            {
                var response = await authService.RegisterAsync(request);
                return Results.Created($"/auth/users/{response.User.Id}", response);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Dữ liệu không hợp lệ",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }).AllowAnonymous();

        group.MapGet("/me", async (ClaimsPrincipal user, IAuthService authService) =>
        {
            var userIdStr = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value;

            if (!Guid.TryParse(userIdStr, out var userId))
                return Results.Unauthorized();

            try
            {
                var userDto = await authService.GetCurrentUserAsync(userId);
                return Results.Ok(userDto);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        }).RequireAuthorization("Authenticated");

        group.MapGet("/roles", async (IAuthService authService) =>
        {
            var roles = await authService.GetRolesAsync();
            return Results.Ok(roles);
        }).RequirePermission(PermissionsCatalog.RolesManage);

        group.MapPost("/roles", async (CreateRoleRequest request, IAuthService authService) =>
        {
            try
            {
                var role = await authService.CreateRoleAsync(request);
                return Results.Created($"/auth/roles/{role.Id}", role);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Dữ liệu không hợp lệ",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequirePermission(PermissionsCatalog.RolesManage);

        group.MapGet("/permissions", async (IAuthService authService) =>
        {
            var permissions = await authService.GetPermissionsAsync();
            return Results.Ok(permissions);
        }).RequirePermission(PermissionsCatalog.RolesManage);

        group.MapPost("/roles/{id:guid}/permissions", async (Guid id, UpdateRolePermissionsRequest request, IAuthService authService) =>
        {
            try
            {
                var role = await authService.UpdateRolePermissionsAsync(id, request);
                return Results.Ok(role);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        }).RequirePermission(PermissionsCatalog.RolesManage);

        group.MapPost("/users/{id:guid}/roles", async (Guid id, AssignUserRolesRequest request, IAuthService authService) =>
        {
            try
            {
                var user = await authService.AssignUserRolesAsync(id, request);
                return Results.Ok(user);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        }).RequirePermission(PermissionsCatalog.UsersManage);

        return app;
    }
}
