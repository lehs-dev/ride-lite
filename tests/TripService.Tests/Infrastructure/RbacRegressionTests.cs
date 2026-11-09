using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SwiftRide.Security;
using Testcontainers.MsSql;
using TripService.Application.DTOs;
using TripService.Application.Services;
using TripService.Domain.Entities;
using TripService.Infrastructure.Persistence;

namespace TripService.Tests.Infrastructure;

/// <summary>
/// P0-5 trên SQL Server thật: danh sách seed phải phủ PermissionsCatalog, rider đọc được
/// payment của mình, và đăng ký công khai không tự cấp Admin được.
/// </summary>
public class RbacRegressionTests : IAsyncLifetime
{
    private readonly MsSqlContainer _mssql =
        new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _mssql.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TripDbContext>(options => options.UseSqlServer(_mssql.GetConnectionString()));
        services.AddScoped<IAuthService, AuthService>();
        _provider = services.BuildServiceProvider(validateScopes: false);

        await using var scope = _provider.CreateAsyncScope();
        await TripDbSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<TripDbContext>());
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _mssql.DisposeAsync();
    }

    [Fact]
    public async Task Seed_CoversEveryPermissionInCatalog()
    {
        // Seed thiếu là nguồn bug "endpoint đòi quyền không ai có": payments.admin, trips.admin,
        // users.read nằm trong catalog nhưng không từng được seed.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();

        var codes = await db.Permissions.Select(p => p.Code).ToListAsync();

        foreach (var expected in PermissionsCatalog.All)
            Assert.Contains(expected, codes);
    }

    [Fact]
    public async Task Rider_CanReadAndCreatePayments()
    {
        // Thiếu payments.read thì mọi GET /payments của rider là 403 dù chính họ vừa trả tiền.
        var permissions = await PermissionsOfAsync("Rider");

        Assert.Contains(PermissionsCatalog.PaymentsCreate, permissions);
        Assert.Contains(PermissionsCatalog.PaymentsRead, permissions);
    }

    [Fact]
    public async Task Admin_HoldsAdminScopedPermissions()
    {
        var permissions = await PermissionsOfAsync("Admin");

        Assert.Contains(PermissionsCatalog.PaymentsAdmin, permissions);
        Assert.Contains(PermissionsCatalog.TripsAdmin, permissions);
        Assert.Contains(PermissionsCatalog.UsersRead, permissions);
    }

    [Fact]
    public async Task Seed_RunTwice_DoesNotDuplicatePermissionsOrGrants()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            await TripDbSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<TripDbContext>());
        }

        await using var check = _provider.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<TripDbContext>();
        var codes = await db.Permissions.Select(p => p.Code).ToListAsync();

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("admin")]
    [InlineData("RolesManage")]
    [InlineData("")]
    [InlineData(null)]
    public async Task PublicRegistration_NeverGrantsPrivilegedRole(string? requestedRole)
    {
        // P0-5: request.Role từng được tra thẳng vào DB, nên POST {"role":"Admin"} là tự cấp
        // quyền vào /admin/payments và /admin/users.
        var username = $"escalation-{Guid.NewGuid():N}";
        var service = NewAuthService();

        var response = await service.RegisterAsync(new RegisterRequest(
            username, "Str0ng!Pass", "Người Dùng Mới", $"{username}@local.test", requestedRole));

        Assert.Equal(["Rider"], response.User.Roles);
        Assert.DoesNotContain(PermissionsCatalog.RolesManage, response.User.Permissions);
        Assert.DoesNotContain(PermissionsCatalog.PaymentsAdmin, response.User.Permissions);
    }

    [Theory]
    [InlineData("Driver", "Driver")]
    [InlineData("Rider", "Rider")]
    [InlineData("driver", "Driver")]
    public async Task PublicRegistration_AllowsSelfRegistrableRoles(string requested, string expected)
    {
        var username = $"rider-{Guid.NewGuid():N}";
        var service = NewAuthService();

        var response = await service.RegisterAsync(new RegisterRequest(
            username, "Str0ng!Pass", "Người Dùng Mới", $"{username}@local.test", requested));

        Assert.Equal([expected], response.User.Roles);
    }

    private AuthService NewAuthService()
    {
        var scope = _provider.CreateAsyncScope();
        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(t => t.GenerateToken(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>?>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<TimeSpan?>()))
            .Returns("test-token");

        return new AuthService(scope.ServiceProvider.GetRequiredService<TripDbContext>(), tokenService.Object);
    }

    private async Task<List<string>> PermissionsOfAsync(string roleName)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TripDbContext>();

        var role = await db.Roles
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .SingleAsync(r => r.Name == roleName);

        return role.RolePermissions.Select(rp => rp.Permission.Code).Distinct().ToList();
    }
}
