using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftRide.Security;

namespace SwiftRide.Security.Tests;

public class AuthenticationExtensionsTests
{
    private const string ValidSecretKey = "SuperSecretKeyForDevelopmentAndTestingSwiftRide2026!@";

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider BuildServices(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwiftRideAuthentication(Config(values));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Valid_symmetric_jwt_config_registers_properly()
    {
        using var provider = BuildServices(new()
        {
            ["Authentication:Issuer"] = "SwiftRide",
            ["Authentication:Audience"] = "swiftride-api",
            ["Authentication:SecretKey"] = ValidSecretKey,
            ["Authentication:RequireHttpsMetadata"] = "false"
        });

        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal("SwiftRide", options.TokenValidationParameters.ValidIssuer);
        Assert.Equal("swiftride-api", options.TokenValidationParameters.ValidAudience);
        Assert.NotNull(options.TokenValidationParameters.IssuerSigningKey);
        Assert.False(options.RequireHttpsMetadata);
    }

    [Fact]
    public void Missing_secret_key_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddSwiftRideAuthentication(Config(new()
            {
                ["Authentication:Issuer"] = "SwiftRide",
                ["Authentication:Audience"] = "swiftride-api"
            })));

        Assert.Contains("SecretKey", ex.Message);
    }

    [Fact]
    public void Secret_key_shorter_than_32_bytes_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddSwiftRideAuthentication(Config(new()
            {
                ["Authentication:SecretKey"] = "short-key-123"
            })));

        Assert.Contains("256 bits", ex.Message);
    }

    [Fact]
    public void TokenService_generates_and_validates_token()
    {
        var config = Config(new()
        {
            ["Authentication:Issuer"] = "SwiftRide",
            ["Authentication:Audience"] = "swiftride-api",
            ["Authentication:SecretKey"] = ValidSecretKey
        });

        var tokenService = new JwtTokenService(config);

        var token = tokenService.GenerateToken(
            userId: "user-123",
            username: "test-rider",
            roles: ["rider"],
            permissions: ["trips.create", "trips.read"],
            email: "rider@swiftride.local");

        Assert.NotNull(token);

        var principal = tokenService.ValidateToken(token);
        Assert.NotNull(principal);
        Assert.Equal("user-123", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("test-rider", principal.FindFirst("name")?.Value);
        Assert.True(principal.IsInRole("rider"));

        var permissions = principal.FindAll("permission").Select(c => c.Value).ToList();
        Assert.Contains("trips.create", permissions);
        Assert.Contains("trips.read", permissions);
        Assert.DoesNotContain("trips.accept", permissions);
    }

    [Fact]
    public async Task PermissionHandler_allows_user_with_permission()
    {
        var handler = new PermissionAuthorizationHandler();
        var requirement = new PermissionRequirement("trips.create");

        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim("permission", "trips.create"));
        var principal = new ClaimsPrincipal(identity);

        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task PermissionHandler_allows_admin_even_without_explicit_permission()
    {
        var handler = new PermissionAuthorizationHandler();
        var requirement = new PermissionRequirement("trips.cancel");

        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        var principal = new ClaimsPrincipal(identity);

        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task PermissionHandler_denies_user_lacking_permission()
    {
        var handler = new PermissionAuthorizationHandler();
        var requirement = new PermissionRequirement("trips.create");

        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim("permission", "other.permission"));
        var principal = new ClaimsPrincipal(identity);

        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task PermissionPolicyProvider_creates_policy_for_permission_prefix()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AuthorizationOptions());
        var provider = new PermissionPolicyProvider(options);

        var policy = await provider.GetPolicyAsync("permission:trips.create");

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is PermissionRequirement pr && pr.Permission == "trips.create");
    }
}
