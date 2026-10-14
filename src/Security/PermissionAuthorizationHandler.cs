using Microsoft.AspNetCore.Authorization;

namespace SwiftRide.Security;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    public const string PermissionClaimType = "permission";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity is not { IsAuthenticated: true })
            return Task.CompletedTask;

        // Admin có toàn quyền trên toàn bộ permissions
        if (context.User.IsInRole("Admin") || context.User.IsInRole("admin"))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Kiểm tra claim permission cụ thể
        var hasPermission = context.User.FindAll(PermissionClaimType)
            .Any(c => string.Equals(c.Value, requirement.Permission, StringComparison.OrdinalIgnoreCase));

        if (hasPermission)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
