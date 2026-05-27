namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Mints JWTs for authenticated drivers (and later, managers).
/// Validation is handled by ASP.NET's built-in JWT middleware — see Program.cs.
/// </summary>
public interface IJwtTokenService
{
    DriverTokenResult IssueDriverToken(Guid driverId, Guid parkId, string phoneHash);
}

public record DriverTokenResult(
    string Token,
    DateTime ExpiresAt);
