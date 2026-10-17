namespace PayTaxi.Core.Interfaces;

/// <summary>
/// Mints JWTs for authenticated drivers (and later, managers).
/// Validation is handled by ASP.NET's built-in JWT middleware — see Program.cs.
/// </summary>
public interface IJwtTokenService
{
    /// <param name="sessionId">
    /// The trusted-device session (DriverSessions) the token is minted from. Sign-in and refresh always name it, so the
    /// token stops working with its session (signed out, revoked by a suspension or a phone change). Null only for tokens
    /// minted outside a session (the tests): those are checked against the driver's status and phone alone.
    /// </param>
    DriverTokenResult IssueDriverToken(Guid driverId, Guid parkId, string phoneHash, Guid? sessionId = null);

    /// <summary>
    /// Mints an admin JWT. <paramref name="parkId"/> is null for super-admin,
    /// set for park-scoped manager accounts.
    /// </summary>
    DriverTokenResult IssueAdminToken(Guid adminUserId, string email, string role, Guid? parkId);
}

public record DriverTokenResult(
    string Token,
    DateTime ExpiresAt);
