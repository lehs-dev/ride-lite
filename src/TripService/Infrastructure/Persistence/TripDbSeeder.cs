using Microsoft.EntityFrameworkCore;
using SwiftRide.Security;
using TripService.Domain.Entities;
using TripService.Infrastructure.Security;

namespace TripService.Infrastructure.Persistence;

public static class TripDbSeeder
{
    public static async Task SeedAsync(TripDbContext db)
    {
        // Tự động tạo schema bảng nếu chưa tồn tại
        // await db.Database.EnsureCreatedAsync();

        // 1. Seed Permissions. Danh sách này PHẢI phủ toàn bộ PermissionsCatalog.All —
        //    TripDbSeederTests kiểm chứng, nên thêm permission mà quên seed là đỏ test ngay.
        //    Trước đây list thiếu trips.admin/payments.admin/users.read: endpoint đòi quyền là
        //    404 với role thường và chỉ "tình cờ" chạy được cho Admin nhờ role bypass.
        var permissionsToSeed = new List<(string Code, string Description, string Module)>
        {
            (PermissionsCatalog.TripsCreate, "Tạo yêu cầu chuyến đi mới", "Trips"),
            (PermissionsCatalog.TripsRead, "Xem thông tin chuyến đi", "Trips"),
            (PermissionsCatalog.TripsAccept, "Tài xế nhận chuyến đi", "Trips"),
            (PermissionsCatalog.TripsUpdateStatus, "Cập nhật trạng thái chuyến đi", "Trips"),
            (PermissionsCatalog.TripsCancel, "Hủy chuyến đi", "Trips"),
            (PermissionsCatalog.TripsAdmin, "Quản trị toàn bộ chuyến đi", "Trips"),
            (PermissionsCatalog.MatchingQuote, "Yêu cầu báo giá ước tính", "Matching"),
            (PermissionsCatalog.MatchingMatch, "Tìm kiếm và ghép nối tài xế", "Matching"),
            (PermissionsCatalog.PaymentsCreate, "Thực hiện thanh toán chuyến đi", "Payments"),
            (PermissionsCatalog.PaymentsRead, "Xem thông tin giao dịch thanh toán", "Payments"),
            (PermissionsCatalog.PaymentsAdmin, "Tra soát toàn bộ giao dịch thanh toán", "Payments"),
            (PermissionsCatalog.RolesManage, "Quản lý vai trò và phân quyền", "Identity"),
            (PermissionsCatalog.UsersManage, "Quản lý người dùng và phân vai trò", "Identity"),
            (PermissionsCatalog.UsersRead, "Xem danh sách người dùng", "Identity"),
        };

        var existingPermCodes = await db.Permissions.Select(p => p.Code).ToListAsync();

        foreach (var (code, desc, module) in permissionsToSeed)
        {
            if (!existingPermCodes.Contains(code))
            {
                db.Permissions.Add(new Permission
                {
                    Code = code,
                    Description = desc,
                    Module = module
                });
            }
        }
        await db.SaveChangesAsync();

        var allPermissions = await db.Permissions.ToDictionaryAsync(p => p.Code);

        // 2. Seed Roles
        var adminRole = await db.Roles.Include(r => r.RolePermissions).FirstOrDefaultAsync(r => r.Name == "Admin");
        if (adminRole == null)
        {
            adminRole = new Role
            {
                Name = "Admin",
                Description = "Quản trị viên toàn hệ thống",
                IsSystemRole = true
            };
            db.Roles.Add(adminRole);
            await db.SaveChangesAsync();
        }

        var riderRole = await db.Roles.Include(r => r.RolePermissions).FirstOrDefaultAsync(r => r.Name == "Rider");
        if (riderRole == null)
        {
            riderRole = new Role
            {
                Name = "Rider",
                Description = "Hành khách đặt chuyến đi",
                IsSystemRole = true
            };
            db.Roles.Add(riderRole);
            await db.SaveChangesAsync();
        }

        var driverRole = await db.Roles.Include(r => r.RolePermissions).FirstOrDefaultAsync(r => r.Name == "Driver");
        if (driverRole == null)
        {
            driverRole = new Role
            {
                Name = "Driver",
                Description = "Tài xế đối tác nhận chuyến",
                IsSystemRole = true
            };
            db.Roles.Add(driverRole);
            await db.SaveChangesAsync();
        }

        // 3. Map Role Permissions
        // Admin: có tất cả permissions
        var currentAdminPermIds = adminRole.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();
        foreach (var perm in allPermissions.Values)
        {
            if (!currentAdminPermIds.Contains(perm.Id))
            {
                db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = perm.Id });
            }
        }

        // Rider: trips.create, trips.read, trips.cancel, matching.quote, payments.create, payments.read.
        // Thiếu payments.read thì mọi GET /payments của rider trả 404/403 dù chính họ vừa trả tiền.
        var riderPermCodes = new[]
        {
            PermissionsCatalog.TripsCreate,
            PermissionsCatalog.TripsRead,
            PermissionsCatalog.TripsCancel,
            PermissionsCatalog.MatchingQuote,
            PermissionsCatalog.PaymentsCreate,
            PermissionsCatalog.PaymentsRead
        };
        var currentRiderPermIds = riderRole.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();
        foreach (var code in riderPermCodes)
        {
            if (allPermissions.TryGetValue(code, out var perm) && !currentRiderPermIds.Contains(perm.Id))
            {
                db.RolePermissions.Add(new RolePermission { RoleId = riderRole.Id, PermissionId = perm.Id });
            }
        }

        // Driver: trips.read, trips.accept, trips.update_status, matching.match
        var driverPermCodes = new[]
        {
            PermissionsCatalog.TripsRead,
            PermissionsCatalog.TripsAccept,
            PermissionsCatalog.TripsUpdateStatus,
            PermissionsCatalog.MatchingMatch
        };
        var currentDriverPermIds = driverRole.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();
        foreach (var code in driverPermCodes)
        {
            if (allPermissions.TryGetValue(code, out var perm) && !currentDriverPermIds.Contains(perm.Id))
            {
                db.RolePermissions.Add(new RolePermission { RoleId = driverRole.Id, PermissionId = perm.Id });
            }
        }
        await db.SaveChangesAsync();

        // 4. Seed Default Users
        if (!await db.Users.AnyAsync(u => u.Username == "admin"))
        {
            var (hash, salt) = PasswordHasher.HashPassword("Admin@123");
            var adminUser = new User
            {
                Username = "admin",
                PasswordHash = hash,
                PasswordSalt = salt,
                FullName = "He Thong Admin",
                Email = "admin@swiftride.local"
            };
            db.Users.Add(adminUser);
            await db.SaveChangesAsync();
            db.UserRoles.Add(new UserRole { UserId = adminUser.Id, RoleId = adminRole.Id });
        }

        if (!await db.Users.AnyAsync(u => u.Username == "test-rider"))
        {
            var (hash, salt) = PasswordHasher.HashPassword("Rider@123");
            var riderUser = new User
            {
                Username = "test-rider",
                PasswordHash = hash,
                PasswordSalt = salt,
                FullName = "Nguyen Van Rider",
                Email = "rider@swiftride.local"
            };
            db.Users.Add(riderUser);
            await db.SaveChangesAsync();
            db.UserRoles.Add(new UserRole { UserId = riderUser.Id, RoleId = riderRole.Id });
        }

        if (!await db.Users.AnyAsync(u => u.Username == "test-driver"))
        {
            var (hash, salt) = PasswordHasher.HashPassword("Driver@123");
            var driverUser = new User
            {
                Username = "test-driver",
                PasswordHash = hash,
                PasswordSalt = salt,
                FullName = "Tran Van Driver",
                Email = "driver@swiftride.local"
            };
            db.Users.Add(driverUser);
            await db.SaveChangesAsync();
            db.UserRoles.Add(new UserRole { UserId = driverUser.Id, RoleId = driverRole.Id });
        }

        await db.SaveChangesAsync();
    }
}