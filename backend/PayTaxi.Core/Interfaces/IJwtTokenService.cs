namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Mints JWTs for authenticated drivers (and later, managers).
/// Validation is handled by ASP.NET's built-in JWT middleware — see Program.cs.
/// </summary>
public interface IJwtTokenService
{
    DriverTokenResult IssueDriverToken(Guid driverId, Guid parkId, string phoneHash);

    /// <summary>
    /// Mints an admin JWT. <paramref name="parkId"/> is null for super-admin,
    /// set for park-scoped manager accounts.
    /// </summary>
    DriverTokenResult IssueAdminToken(Guid adminUserId, string email, string role, Guid? parkId);
}

public record DriverTokenResult(
    string Token,
    DateTime ExpiresAt);
