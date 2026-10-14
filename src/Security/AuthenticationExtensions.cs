using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace SwiftRide.Security;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddSwiftRideAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication");
        var issuer = section["Issuer"] ?? "SwiftRide";
        var audience = section["Audience"] ?? "swiftride-api";
        var secretKey = section["SecretKey"];

        if (string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Missing required configuration: Authentication:SecretKey.");

        if (Encoding.UTF8.GetByteCount(secretKey) < 32)
            throw new InvalidOperationException("Authentication:SecretKey must be at least 256 bits (32 bytes).");

        var requireHttpsMetadata = section.GetValue("RequireHttpsMetadata", false);
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = requireHttpsMetadata;
                options.SaveToken = true;
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                    RoleClaimType = "role"
                };
            });

        // Dynamic RBAC Permission Authorization
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<ITokenService, JwtTokenService>();

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy("Authenticated", policy => policy.RequireAuthenticatedUser())
            .AddPolicy("Rider", policy => policy.RequireRole("rider", "Rider"))
            .AddPolicy("Driver", policy => policy.RequireRole("driver", "Driver"))
            .AddPolicy("Admin", policy => policy.RequireRole("admin", "Admin"));

        return services;
    }
}
