namespace TripService.Application.DTOs;

public record LoginRequest(string Username, string Password);

public record RegisterRequest(
    string Username,
    string Password,
    string FullName,
    string Email,
    string? Role = "Rider");

public record AuthResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    UserDto User);

public record UserDto(
    Guid Id,
    string Username,
    string FullName,
    string Email,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

public record UserSummaryDto(
    Guid Id,
    string Username,
    string FullName,
    string Email,
    bool IsActive,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> Roles);

public record RoleDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystemRole,
    IReadOnlyList<string> Permissions);

public record PermissionDto(
    Guid Id,
    string Code,
    string Description,
    string Module);

public record CreateRoleRequest(
    string Name,
    string? Description,
    List<string>? PermissionCodes);

public record UpdateRolePermissionsRequest(
    List<string> PermissionCodes);

public record AssignUserRolesRequest(
    List<string> RoleNames);

public record UpdateUserStatusRequest(
    bool IsActive);

public record AdminTripDto(
    Guid Id,
    Guid RiderId,
    Guid? DriverId,
    string Status,
    decimal EstimatedPrice,
    DateTime CreatedAtUtc);
