using TripService.Application.DTOs;

namespace TripService.Application.Services;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request);
    Task<AuthResponse> AdminLoginAsync(LoginRequest request);
    Task<AuthResponse> RegisterAsync(RegisterRequest request);
    Task<UserDto> GetCurrentUserAsync(Guid userId);
    Task<IReadOnlyList<RoleDto>> GetRolesAsync();
    Task<RoleDto> CreateRoleAsync(CreateRoleRequest request);
    Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync();
    Task<RoleDto> UpdateRolePermissionsAsync(Guid roleId, UpdateRolePermissionsRequest request);
    Task<UserDto> AssignUserRolesAsync(Guid userId, AssignUserRolesRequest request);
    Task<IReadOnlyList<UserSummaryDto>> GetUsersAsync(string? role = null, string? keyword = null);
    Task<UserDto> UpdateUserStatusAsync(Guid targetUserId, bool isActive, Guid currentAdminId);
}
