using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// Mints driver JWTs using the symmetric key configured under "Jwt".
/// Claims layout:
///   sub        — driverId (Guid)
///   parkId     — parkId  (Guid)
///   phoneHash  — SHA-256 of phone, useful for OTP rate-limiting on later requests
///   role       — "driver"
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private readonly string _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryMinutes;

    public JwtTokenService(IConfiguration config)
    {
        var section = config.GetSection("Jwt");
        _key = section["Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
        _issuer = section["Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer missing");
        _audience = section["Audience"] ?? throw new InvalidOperationException("Jwt:Audience missing");
        _expiryMinutes = section.GetValue("ExpiryMinutes", 1440);
    }

    public DriverTokenResult IssueDriverToken(Guid driverId, Guid parkId, string phoneHash)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, driverId.ToString()),
            new("parkId",    parkId.ToString()),
            new("phoneHash", phoneHash),
            new(ClaimTypes.Role, "driver"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        return BuildToken(claims);
    }

    public DriverTokenResult IssueAdminToken(Guid adminUserId, string email, string role, Guid? parkId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, adminUserId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(ClaimTypes.Role, "admin"),
            new("adminScope", role), // "super_admin" or "park_admin"
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (parkId is not null)
            claims.Add(new Claim("parkId", parkId.Value.ToString()));
        return BuildToken(claims);
    }

    private DriverTokenResult BuildToken(IEnumerable<Claim> claims)
    {
        var expires = DateTime.UtcNow.AddMinutes(_expiryMinutes);
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key)),
            SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: _issuer, audience: _audience,
            claims: claims, expires: expires,
            signingCredentials: creds);
        return new DriverTokenResult(
            new JwtSecurityTokenHandler().WriteToken(jwt),
            expires);
    }
}
