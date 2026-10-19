using System.Security.Claims;

namespace SwiftRide.Security;

public interface ITokenService
{
    string GenerateToken(
        string userId,
        string username,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null,
        string? email = null,
        TimeSpan? expiry = null);

    ClaimsPrincipal? ValidateToken(string token);
}
