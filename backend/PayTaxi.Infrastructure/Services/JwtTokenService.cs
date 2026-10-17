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
///   phoneHash  — the phone's keyed lookup hash (LookupHash; opaque without the hash key). DriverTokenCheck compares
///                it with the driver's: a phone change ends the token at once. (A hash-key rotation re-hashes every
///                phone, so outstanding access tokens then fail once and the app refreshes — refresh does not compare.)
///   sessionId  — the DriverSessions row it was minted from (sign-in, refresh): it dies with the session
///   role       — "driver"
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    /// <summary>The driver token's claim naming its session (see the class summary).</summary>
    public const string DriverSessionClaim = "sessionId";

    private readonly string _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryMinutes;
    private readonly int _driverAccessMinutes;

    public JwtTokenService(IConfiguration config)
    {
        var section = config.GetSection("Jwt");
        _key = section["Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
        _issuer = section["Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer missing");
        _audience = section["Audience"] ?? throw new InvalidOperationException("Jwt:Audience missing");
        _expiryMinutes = section.GetValue("ExpiryMinutes", 1440);
        // Drivers get short access tokens; the 90-day trusted-device session re-mints them.
        _driverAccessMinutes = section.GetValue("DriverAccessMinutes", 60);
    }

    public DriverTokenResult IssueDriverToken(Guid driverId, Guid parkId, string phoneHash, Guid? sessionId = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, driverId.ToString()),
            new("parkId",    parkId.ToString()),
            new("phoneHash", phoneHash),
            new(ClaimTypes.Role, "driver"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        // A claim name of our own: "sid" is in the JWT handler's inbound claim map and would arrive renamed.
        if (sessionId is { } sid) claims.Add(new Claim(DriverSessionClaim, sid.ToString()));
        return BuildToken(claims, _driverAccessMinutes);
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
        return BuildToken(claims, _expiryMinutes);
    }

    private DriverTokenResult BuildToken(IEnumerable<Claim> claims, int minutes)
    {
        var expires = DateTime.UtcNow.AddMinutes(minutes);
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
