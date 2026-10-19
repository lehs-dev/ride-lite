using Microsoft.EntityFrameworkCore;
using SwiftRide.Security;
using TripService.Application.DTOs;
using TripService.Domain.Entities;
using TripService.Infrastructure.Persistence;
using TripService.Infrastructure.Security;

namespace TripService.Application.Services;

public class AuthService : IAuthService
{
    private readonly TripDbContext _db;
    private readonly ITokenService _tokenService;

    public AuthService(TripDbContext db, ITokenService tokenService)
    {
        _db = db;
        _tokenService = tokenService;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Username == request.Username);

        if (user == null || !user.IsActive ||
            !PasswordHasher.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt))
        {
            throw new UnauthorizedAccessException("Tài khoản hoặc mật khẩu không chính xác.");
        }

        var roles = user.UserRoles.Select(ur => ur.Role.Name).Distinct().ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToList();

        var token = _tokenService.GenerateToken(
            userId: user.Id.ToString(),
            username: user.Username,
            roles: roles,
            permissions: permissions,
            email: user.Email);

        var userDto = new UserDto(
            user.Id,
            user.Username,
            user.FullName,
            user.Email,
            roles,
            permissions);

        return new AuthResponse(token, "Bearer", 3600, userDto);
    }

    public async Task<AuthResponse> AdminLoginAsync(LoginRequest request)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Username == request.Username);

        if (user == null || !user.IsActive ||
            !PasswordHasher.VerifyPassword(request.Password, user.PasswordHash, user.PasswordSalt))
        {
            throw new UnauthorizedAccessException("Tài khoản hoặc mật khẩu không chính xác.");
        }

        var roles = user.UserRoles.Select(ur => ur.Role.Name).Distinct().ToList();

        // Kiểm tra xem user có vai trò Admin hay không
        var isAdmin = roles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
        if (!isAdmin)
        {
            // Ném ngoại lệ riêng để API trả về 403 Forbidden
            throw new AccessViolationException("Tài khoản không có quyền truy cập trang quản trị.");
        }

        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToList();

        // Token quản trị có thời hạn ngắn hơn để tăng cường an toàn (30 phút)
        var token = _tokenService.GenerateToken(
            userId: user.Id.ToString(),
            username: user.Username,
            roles: roles,
            permissions: permissions,
            email: user.Email,
            expiry: TimeSpan.FromMinutes(30));

        var userDto = new UserDto(
            user.Id,
            user.Username,
            user.FullName,
            user.Email,
            roles,
            permissions);

        return new AuthResponse(token, "Bearer", 1800, userDto);
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        if (await _db.Users.AnyAsync(u => u.Username == request.Username))
            throw new InvalidOperationException($"Tên đăng nhập '{request.Username}' đã tồn tại.");

        if (await _db.Users.AnyAsync(u => u.Email == request.Email))
            throw new InvalidOperationException($"Email '{request.Email}' đã được đăng ký.");

        var roleName = string.IsNullOrWhiteSpace(request.Role) ? "Rider" : request.Role.Trim();
        var role = await _db.Roles
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(r => r.Name.ToLower() == roleName.ToLower());

        if (role == null)
        {
            // Mặc định fallback về Rider nếu role không tồn tại hoặc chỉ định sai
            role = await _db.Roles
                .Include(r => r.RolePermissions)
                    .ThenInclude(rp => rp.Permission)
                .FirstAsync(r => r.Name == "Rider");
        }

        var (hash, salt) = PasswordHasher.HashPassword(request.Password);
        var user = new User
        {
            Username = request.Username,
            PasswordHash = hash,
            PasswordSalt = salt,
            FullName = request.FullName,
            Email = request.Email,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await _db.SaveChangesAsync();

        var roles = new List<string> { role.Name };
        var permissions = role.RolePermissions.Select(rp => rp.Permission.Code).Distinct().ToList();

        var token = _tokenService.GenerateToken(
            userId: user.Id.ToString(),
            username: user.Username,
            roles: roles,
            permissions: permissions,
            email: user.Email);

        var userDto = new UserDto(
            user.Id,
            user.Username,
            user.FullName,
            user.Email,
            roles,
            permissions);

        return new AuthResponse(token, "Bearer", 3600, userDto);
    }

    public async Task<UserDto> GetCurrentUserAsync(Guid userId)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            throw new KeyNotFoundException("Không tìm thấy thông tin người dùng.");

        var roles = user.UserRoles.Select(ur => ur.Role.Name).Distinct().ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToList();

        return new UserDto(
            user.Id,
            user.Username,
            user.FullName,
            user.Email,
            roles,
            permissions);
    }

    public async Task<IReadOnlyList<UserSummaryDto>> GetUsersAsync(string? role = null, string? keyword = null)
    {
        var query = _db.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(role))
        {
            query = query.Where(u => u.UserRoles.Any(ur => ur.Role.Name.ToLower() == role.Trim().ToLower()));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim().ToLower();
            query = query.Where(u =>
                u.Username.ToLower().Contains(kw) ||
                u.FullName.ToLower().Contains(kw) ||
                u.Email.ToLower().Contains(kw));
        }

        var users = await query
            .OrderByDescending(u => u.CreatedAtUtc)
            .ToListAsync();

        return users.Select(u => new UserSummaryDto(
            u.Id,
            u.Username,
            u.FullName,
            u.Email,
            u.IsActive,
            u.CreatedAtUtc,
            u.UserRoles.Select(ur => ur.Role.Name).ToList())).ToList();
    }

    public async Task<UserDto> UpdateUserStatusAsync(Guid targetUserId, bool isActive, Guid currentAdminId)
    {
        if (targetUserId == currentAdminId)
        {
            throw new InvalidOperationException("Quản trị viên không thể tự khóa tài khoản của chính mình.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId);
        if (user == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy người dùng với Id '{targetUserId}'.");
        }

        user.IsActive = isActive;
        await _db.SaveChangesAsync();

        return await GetCurrentUserAsync(targetUserId);
    }

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync()
    {
        var roles = await _db.Roles
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .ToListAsync();

        return roles.Select(r => new RoleDto(
            r.Id,
            r.Name,
            r.Description,
            r.IsSystemRole,
            r.RolePermissions.Select(rp => rp.Permission.Code).ToList())).ToList();
    }

    public async Task<RoleDto> CreateRoleAsync(CreateRoleRequest request)
    {
        if (await _db.Roles.AnyAsync(r => r.Name.ToLower() == request.Name.ToLower()))
            throw new InvalidOperationException($"Vai trò '{request.Name}' đã tồn tại.");

        var role = new Role
        {
            Name = request.Name,
            Description = request.Description,
            IsSystemRole = false
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        if (request.PermissionCodes != null && request.PermissionCodes.Count > 0)
        {
            var permissions = await _db.Permissions
                .Where(p => request.PermissionCodes.Contains(p.Code))
                .ToListAsync();

            foreach (var perm in permissions)
            {
                _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
            }
            await _db.SaveChangesAsync();
        }

        var assignedPerms = await _db.RolePermissions
            .Where(rp => rp.RoleId == role.Id)
            .Include(rp => rp.Permission)
            .Select(rp => rp.Permission.Code)
            .ToListAsync();

        return new RoleDto(role.Id, role.Name, role.Description, role.IsSystemRole, assignedPerms);
    }

    public async Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync()
    {
        var permissions = await _db.Permissions.ToListAsync();
        return permissions.Select(p => new PermissionDto(p.Id, p.Code, p.Description, p.Module)).ToList();
    }

    public async Task<RoleDto> UpdateRolePermissionsAsync(Guid roleId, UpdateRolePermissionsRequest request)
    {
        var role = await _db.Roles
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == roleId);

        if (role == null)
            throw new KeyNotFoundException($"Không tìm thấy vai trò với Id '{roleId}'.");

        // Xóa các permissions cũ
        _db.RolePermissions.RemoveRange(role.RolePermissions);

        // Gán permissions mới
        var permissions = await _db.Permissions
            .Where(p => request.PermissionCodes.Contains(p.Code))
            .ToListAsync();

        foreach (var perm in permissions)
        {
            _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
        }

        await _db.SaveChangesAsync();

        return new RoleDto(
            role.Id,
            role.Name,
            role.Description,
            role.IsSystemRole,
            permissions.Select(p => p.Code).ToList());
    }

    public async Task<UserDto> AssignUserRolesAsync(Guid userId, AssignUserRolesRequest request)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
            throw new KeyNotFoundException($"Không tìm thấy người dùng với Id '{userId}'.");

        _db.UserRoles.RemoveRange(user.UserRoles);

        var roles = await _db.Roles
            .Where(r => request.RoleNames.Contains(r.Name))
            .ToListAsync();

        foreach (var role in roles)
        {
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }

        await _db.SaveChangesAsync();

        return await GetCurrentUserAsync(userId);
    }
}
