using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace SwiftRide.Security;

public class JwtTokenService : ITokenService
{
    private readonly string _issuer;
    private readonly string _audience;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly TimeSpan _defaultLifetime;

    public JwtTokenService(IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication");
        _issuer = section["Issuer"] ?? "SwiftRide";
        _audience = section["Audience"] ?? "swiftride-api";
        var secretKey = section["SecretKey"]
            ?? throw new InvalidOperationException("Missing required configuration: Authentication:SecretKey.");

        if (Encoding.UTF8.GetByteCount(secretKey) < 32)
        {
            throw new InvalidOperationException("Authentication:SecretKey must be at least 256 bits (32 bytes).");
        }

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));

        var lifetimeMinutes = section.GetValue<double?>("TokenLifetimeMinutes") ?? 60;
        _defaultLifetime = TimeSpan.FromMinutes(lifetimeMinutes);
    }

    public string GenerateToken(
        string userId,
        string username,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null,
        string? email = null,
        TimeSpan? expiry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, username),
            new("name", username),
        };

        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new(ClaimTypes.Email, email));
            claims.Add(new(JwtRegisteredClaimNames.Email, email));
        }

        if (roles != null)
        {
            foreach (var role in roles.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                claims.Add(new(ClaimTypes.Role, role));
                claims.Add(new("role", role));
            }
        }

        if (permissions != null)
        {
            foreach (var permission in permissions.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                claims.Add(new("permission", permission));
            }
        }

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var lifetime = expiry ?? _defaultLifetime;
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.Add(lifetime),
            Issuer = _issuer,
            Audience = _audience,
            SigningCredentials = credentials
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(tokenDescriptor);
        return handler.WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _issuer,
            ValidateAudience = true,
            ValidAudience = _audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = ClaimTypes.Role // "role" được inbound-map sang URI này khi validate

        };

        try
        {
            return handler.ValidateToken(token, validationParameters, out _);
        }
        catch
        {
            return null;
        }
    }
}
