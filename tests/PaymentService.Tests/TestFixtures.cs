using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PaymentService.Application.Abstractions;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Persistence;

namespace PaymentService.Tests;

internal static class TestFixtures
{
    /// <summary>
    /// Store InMemory cho unit test chính sách. Transaction bị provider bỏ qua (chỉ warning) và
    /// raw SQL không chạy được — PaymentProcessor có nhánh riêng cho InMemory ở chỗ claim inbox.
    /// </summary>
    public static DbContextOptions<PaymentDbContext> InMemoryOptions() =>
        new DbContextOptionsBuilder<PaymentDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                .InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    /// <summary>Gateway trả về kết quả charge thành công với checksum tự tính lại được.</summary>
    public static GatewayChargeResult Success(decimal amount) => new(
        Success: true,
        Retryable: false,
        Code: "SUCCESS",
        Message: "ok",
        PartnerCode: "SWIFTRIDE_SANDBOX",
        OrderRef: $"SR{Guid.NewGuid():N}"[..24],
        TransId: Guid.NewGuid().ToString("N"),
        Checksum: new string('a', 64));

    public static ClaimsPrincipal Principal(Guid actorId, params string[] roles)
    {
        var identity = new ClaimsIdentity(
            [new Claim("sub", actorId.ToString()), .. roles.Select(r => new Claim("role", r))],
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }
}
