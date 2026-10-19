using Microsoft.AspNetCore.Builder;

namespace SwiftRide.Security;

public static class EndpointSecurityExtensions
{
    /// <summary>
    /// Yêu cầu người dùng phải có quyền (permission) tương ứng.
    /// Quyền này được xác thực thông qua PermissionPolicyProvider và PermissionAuthorizationHandler.
    /// Người dùng có role Admin mặc định thỏa mãn tất cả quyền.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return builder.RequireAuthorization($"{PermissionPolicyProvider.PolicyPrefix}{permission}");
    }
}
